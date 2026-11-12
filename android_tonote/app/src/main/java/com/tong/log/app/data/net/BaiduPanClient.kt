package com.tong.log.app.data.net

import android.net.Uri
import com.tong.log.app.data.store.AppStorage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

class BaiduPanException(message: String) : IOException(message)

/**
 * 百度网盘开放平台客户端：OAuth 换 token、文件列表、分片上传、下载。
 * 上传走 precreate → superfile2 分片 → create，与 Mac 端 BaiduPanClient 一致。
 */
object BaiduPanClient {
    private const val UA = "pan.baidu.com"
    private const val CHUNK = 4 * 1024 * 1024

    private val client = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .writeTimeout(180, TimeUnit.SECONDS)
        .build()

    /** 列目录时顺手记下 路径 → fs_id，供下载走 filemetas 兜底，避免重复列目录。 */
    private val fsIdCache = ConcurrentHashMap<String, Long>()

    /**
     * 百度对部分应用禁止 xpan/file?method=download&path=（errno 20020
     * 「该路径不在允许的访问范围内」）。命中一次后本进程直接走 filemetas，
     * 不再每篇笔记都先发一个必然失败的请求。
     */
    @Volatile
    private var downloadByPathBlocked = false

    // ---------- OAuth ----------

    fun buildAuthorizeUrl(state: String, display: String): String =
        Uri.parse(BaiduPanConfig.AUTHORIZE_URL).buildUpon()
            .appendQueryParameter("response_type", "code")
            .appendQueryParameter("client_id", BaiduPanConfig.appKey)
            .appendQueryParameter("redirect_uri", BaiduPanConfig.REDIRECT_URI)
            .appendQueryParameter("scope", BaiduPanConfig.SCOPE)
            .appendQueryParameter("display", display)
            .appendQueryParameter("state", state)
            .build()
            .toString()

    suspend fun exchangeCode(code: String): BaiduPanToken = withContext(Dispatchers.IO) {
        val form = FormBody.Builder()
            .add("grant_type", "authorization_code")
            .add("code", code)
            .add("client_id", BaiduPanConfig.appKey)
            .add("client_secret", BaiduPanConfig.appSecret)
            .add("redirect_uri", BaiduPanConfig.REDIRECT_URI)
            .build()
        val req = Request.Builder().url(BaiduPanConfig.TOKEN_URL).post(form)
            .header("User-Agent", UA).build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: throw IOException("授权响应为空")
            val parsed = AppStorage.json.decodeFromString<BaiduTokenResponse>(body)
            if (parsed.error != null || parsed.accessToken == null) {
                throw BaiduPanException(parsed.errorDescription ?: parsed.error ?: "授权失败")
            }
            BaiduPanToken(
                accessToken = parsed.accessToken,
                refreshToken = parsed.refreshToken ?: "",
                expiresIn = parsed.expiresIn ?: 0,
                scope = parsed.scope ?: ""
            )
        }
    }

    suspend fun refresh(refreshToken: String): BaiduPanToken = withContext(Dispatchers.IO) {
        val form = FormBody.Builder()
            .add("grant_type", "refresh_token")
            .add("refresh_token", refreshToken.ifEmpty { BaiduPanTokenStore.current()?.refreshToken ?: "" })
            .add("client_id", BaiduPanConfig.appKey)
            .add("client_secret", BaiduPanConfig.appSecret)
            .build()
        val req = Request.Builder().url(BaiduPanConfig.TOKEN_URL).post(form)
            .header("User-Agent", UA).build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: throw IOException("刷新响应为空")
            val parsed = AppStorage.json.decodeFromString<BaiduTokenResponse>(body)
            if (parsed.error != null || parsed.accessToken == null) {
                throw BaiduPanException(parsed.errorDescription ?: parsed.error ?: "刷新失败")
            }
            BaiduPanToken(
                accessToken = parsed.accessToken,
                refreshToken = parsed.refreshToken ?: refreshToken,
                expiresIn = parsed.expiresIn ?: 0,
                scope = parsed.scope ?: ""
            )
        }
    }

    suspend fun validToken(): String {
        val t = BaiduPanTokenStore.current() ?: throw BaiduPanException("未登录")
        return if (t.isExpired) {
            val new = refresh(t.refreshToken)
            BaiduPanTokenStore.save(new)
            new.accessToken
        } else t.accessToken
    }

    // ---------- 账号 / 配额 ----------

    suspend fun userInfo(token: String): UserInfo? = withContext(Dispatchers.IO) {
        val url = Uri.parse(BaiduPanConfig.XPAN_API).buildUpon()
            .appendQueryParameter("method", "uinfo")
            .appendQueryParameter("access_token", token).build().toString()
        val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: return@withContext null
            runCatching { AppStorage.json.decodeFromString<UserInfo>(body) }.getOrNull()
        }
    }

    suspend fun quota(token: String): Quota? = withContext(Dispatchers.IO) {
        val url = Uri.parse(BaiduPanConfig.XPAN_API).buildUpon()
            .appendQueryParameter("method", "quota")
            .appendQueryParameter("access_token", token).build().toString()
        val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: return@withContext null
            runCatching { AppStorage.json.decodeFromString<Quota>(body) }.getOrNull()
        }
    }

    // ---------- 文件列表 / 下载 / 删除 ----------

    /** 返回目录下 JSON 文件名(uuid) → server_mtime 的映射；目录不存在返回空。 */
    suspend fun listJsonFiles(dir: String, token: String): Map<String, Long> = withContext(Dispatchers.IO) {
        val result = mutableMapOf<String, Long>()
        var start = 0
        while (start < 5000) {
            val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon()
                .appendQueryParameter("method", "list")
                .appendQueryParameter("access_token", token)
                .appendQueryParameter("dir", dir)
                .appendQueryParameter("limit", "200")
                .appendQueryParameter("start", start.toString())
                .appendQueryParameter("order", "time")
                .appendQueryParameter("desc", "1")
                .build().toString()
            val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
            val resp = client.newCall(req).execute().use { it }
            if (!resp.isSuccessful) throw BaiduPanException("列目录失败 HTTP ${resp.code}")
            val body = resp.body?.string() ?: break
            val parsed = runCatching { AppStorage.json.decodeFromString<ListResponse>(body) }.getOrNull()
                ?: throw BaiduPanException("列目录响应异常：${body.take(200)}")
            val errno = parsed.errno ?: 0
            // 目录不存在（-9 / 31066）视为空目录，其他错误必须抛出，绝不能静默当成「云端没有笔记」
            if (errno == -9 || errno == 31066) return@withContext emptyMap()
            if (errno != 0) {
                throw BaiduPanException("列目录失败 errno=$errno ${parsed.showMsg ?: ""} dir=$dir")
            }
            if (parsed.list.isEmpty()) break
            for (f in parsed.list) {
                if (f.fsId != 0L) fsIdCache[f.path] = f.fsId
                if (f.isDir == 0 && f.path.endsWith(".json")) {
                    val name = f.path.substringAfterLast('/').removeSuffix(".json")
                    result[name.uppercase()] = f.serverMtime
                }
            }
            if (parsed.list.size < 200) break
            start += parsed.list.size
        }
        result
    }

    suspend fun downloadData(path: String, token: String): ByteArray = withContext(Dispatchers.IO) {
        var dlink: String? = null
        var directError = ""

        // 1) 首选方式与 Mac 端一致：method=download&path= 直接取直链
        if (!downloadByPathBlocked) {
            val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon()
                .appendQueryParameter("method", "download")
                .appendQueryParameter("access_token", token)
                .appendQueryParameter("path", path)
                .appendQueryParameter("threshold", "0")
                .build().toString()
            val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
            val resp = client.newCall(req).execute().use { it }
            val body = resp.body?.string() ?: throw BaiduPanException("下载元信息为空：$path")
            val parsed = runCatching { AppStorage.json.decodeFromString<DownloadResponse>(body) }.getOrNull()
                ?: throw BaiduPanException("下载响应异常：${body.take(200)}")
            dlink = parsed.list.firstOrNull()?.dlink?.takeIf { it.isNotEmpty() }
            if (dlink == null) {
                directError = "errno=${parsed.errno} ${parsed.showMsg ?: ""}".trim()
                if ((parsed.errno ?: 0) != 0) downloadByPathBlocked = true
            }
        }

        // 2) 兜底：path 被开放平台拒绝时，用 fs_id 走 filemetas&dlink=1 取直链
        if (dlink == null) {
            val fsId = cachedFsId(path) ?: findFsId(path, token)
            if (fsId != null) dlink = dlinkByFsId(fsId, token)
        }

        val link = dlink ?: throw BaiduPanException("文件不存在或无权下载：$path $directError")
        val realUrl = Uri.parse(link).buildUpon()
            .appendQueryParameter("access_token", token).build().toString()
        val realReq = Request.Builder().url(realUrl).get().header("User-Agent", UA).build()
        client.newCall(realReq).execute().use { r ->
            if (r.code >= 400) throw BaiduPanException("下载失败 HTTP ${r.code}：$path")
            r.body?.bytes() ?: throw BaiduPanException("下载内容为空：$path")
        }
    }

    /** 云端文件名大小写可能与本地 id 不一致，查缓存时忽略大小写。 */
    private fun cachedFsId(path: String): Long? = fsIdCache[path]
        ?: fsIdCache.entries.firstOrNull { it.key.equals(path, ignoreCase = true) }?.value

    /** 列父目录，按 path 精确匹配拿 fs_id（filemetas 只认 fsids，不认 path）。 */
    private fun findFsId(path: String, token: String): Long? {
        val dir = path.substringBeforeLast('/')
        if (dir.isEmpty()) return null
        var start = 0
        while (start < 5000) {
            val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon()
                .appendQueryParameter("method", "list")
                .appendQueryParameter("access_token", token)
                .appendQueryParameter("dir", dir)
                .appendQueryParameter("limit", "200")
                .appendQueryParameter("start", start.toString())
                .build().toString()
            val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
            val resp = client.newCall(req).execute().use { it }
            val body = resp.body?.string() ?: return null
            val parsed = runCatching { AppStorage.json.decodeFromString<ListResponse>(body) }.getOrNull()
                ?: return null
            if ((parsed.errno ?: 0) != 0 || parsed.list.isEmpty()) return null
            var hit: Long? = null
            for (f in parsed.list) {
                if (f.fsId != 0L) fsIdCache[f.path] = f.fsId
                if (f.path.equals(path, ignoreCase = true) && f.fsId != 0L) hit = f.fsId
            }
            if (hit != null) return hit
            if (parsed.list.size < 200) return null
            start += parsed.list.size
        }
        return null
    }

    /** method=filemetas&fsids=[id]&dlink=1 → 下载直链；返回体是 { info: [ { dlink } ] }。 */
    private fun dlinkByFsId(fsId: Long, token: String): String? {
        val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon()
            .appendQueryParameter("method", "filemetas")
            .appendQueryParameter("access_token", token)
            .appendQueryParameter("fsids", "[$fsId]")
            .appendQueryParameter("dlink", "1")
            .build().toString()
        val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
        val resp = client.newCall(req).execute().use { it }
        val body = resp.body?.string() ?: return null
        val root = runCatching { AppStorage.json.decodeFromString<JsonObject>(body) }.getOrNull()
            ?: return null
        val errno = (root["errno"] as? JsonPrimitive)?.content?.toIntOrNull() ?: 0
        if (errno != 0) return null
        val info = root["info"] as? JsonArray ?: return null
        val first = info.firstOrNull() as? JsonObject ?: return null
        return (first["dlink"] as? JsonPrimitive)?.content?.takeIf { it.isNotEmpty() }
    }

    suspend fun deleteRemote(path: String, token: String) = withContext(Dispatchers.IO) {
        val filelist = "[\"$path\"]"
        val form = FormBody.Builder()
            .add("method", "filemanager")
            .add("opera", "delete")
            .add("async", "0")
            .add("filelist", filelist)
            .add("access_token", token)
            .build()
        val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon().toString()
        val req = Request.Builder().url(url).post(form).header("User-Agent", UA).build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: ""
            runCatching { AppStorage.json.decodeFromString<kotlinx.serialization.json.JsonObject>(body) }
                .getOrNull()?.let {
                    val errno = (it["errno"] as? kotlinx.serialization.json.JsonPrimitive)?.content?.toIntOrNull()
                    if (errno != null && errno != 0 && errno != -9 && errno != 31066) {
                        throw BaiduPanException("删除失败 errno=$errno")
                    }
                }
        }
    }

    // ---------- 上传（分片） ----------

    suspend fun uploadData(data: ByteArray, path: String, token: String) = withContext(Dispatchers.IO) {
        val md5s = mutableListOf<String>()
        var offset = 0
        while (offset < data.size) {
            val end = minOf(offset + CHUNK, data.size)
            md5s.add(md5(data, offset, end))
            offset = end
        }
        val blockList = "[" + md5s.joinToString(",") { "\"$it\"" } + "]"

        // 1) precreate
        val preForm = FormBody.Builder()
            .add("method", "precreate")
            .add("access_token", token)
            .add("path", path)
            .add("size", data.size.toString())
            .add("isdir", "0")
            .add("autoinit", "1")
            .add("block_list", blockList)
            .add("rtype", "3")
            .build()
        val preUrl = Uri.parse(BaiduPanConfig.FILE_API).buildUpon().toString()
        val preResp = client.newCall(
            Request.Builder().url(preUrl).post(preForm).header("User-Agent", UA).build()
        ).execute().use { it }
        if (!preResp.isSuccessful) throw BaiduPanException("precreate HTTP ${preResp.code}")
        val preBody = preResp.body?.string() ?: throw BaiduPanException("precreate 响应为空")
        val pre = runCatching { AppStorage.json.decodeFromString<PrecreateResponse>(preBody) }.getOrNull()
            ?: throw BaiduPanException("precreate 响应异常：${preBody.take(200)}")
        val uploadId = pre.uploadIdValue ?: throw BaiduPanException("precreate 缺 uploadid")

        // 2) 秒传 or 分片上传
        if (pre.returnType != 2) {
            offset = 0
            var seq = 0
            while (offset < data.size) {
                val end = minOf(offset + CHUNK, data.size)
                uploadChunk(data, offset, end, path, uploadId, seq, token)
                offset = end
                seq++
            }
        }

        // 3) create
        val createForm = FormBody.Builder()
            .add("method", "create")
            .add("access_token", token)
            .add("path", path)
            .add("size", data.size.toString())
            .add("isdir", "0")
            .add("uploadid", uploadId)
            .add("block_list", blockList)
            .add("rtype", "3")
            .build()
        val createResp = client.newCall(
            Request.Builder().url(preUrl).post(createForm).header("User-Agent", UA).build()
        ).execute().use { it }
        val createBody = createResp.body?.string() ?: throw BaiduPanException("create 响应为空")
        val created = runCatching { AppStorage.json.decodeFromString<CreateResponse>(createBody) }.getOrNull()
        if (created == null || (created.errno != null && created.errno != 0)) {
            throw BaiduPanException("create 失败：${createBody.take(200)}")
        }
    }

    private fun uploadChunk(
        data: ByteArray, from: Int, to: Int,
        path: String, uploadId: String, seq: Int, token: String
    ) {
        val chunk = data.copyOfRange(from, to)
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart(
                "file", "blob",
                chunk.toRequestBody("application/octet-stream".toMediaType())
            )
            .addFormDataPart("path", path)
            .addFormDataPart("uploadid", uploadId)
            .addFormDataPart("partseq", seq.toString())
            .build()
        val url = Uri.parse(BaiduPanConfig.SUPERFILE2).buildUpon()
            .appendQueryParameter("method", "upload")
            .appendQueryParameter("type", "tmpfile")
            .appendQueryParameter("access_token", token)
            .build().toString()
        client.newCall(
            Request.Builder().url(url).post(body).header("User-Agent", UA).build()
        ).execute().use { resp ->
            if (!resp.isSuccessful) throw BaiduPanException("分片上传失败 HTTP ${resp.code}")
        }
    }

    private fun md5(data: ByteArray, from: Int, to: Int): String {
        val md = MessageDigest.getInstance("MD5")
        md.update(data, from, to - from)
        return md.digest().joinToString("") { "%02x".format(it) }
    }
}

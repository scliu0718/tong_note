package com.tong.log.app.data.net

import android.net.Uri
import com.tong.log.app.data.store.AppStorage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.security.MessageDigest
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
        val req = Request.Builder().url(url).get().build()
        client.newCall(req).execute().use { resp ->
            val body = resp.body?.string() ?: return@withContext null
            runCatching { AppStorage.json.decodeFromString<UserInfo>(body) }.getOrNull()
        }
    }

    suspend fun quota(token: String): Quota? = withContext(Dispatchers.IO) {
        val url = Uri.parse(BaiduPanConfig.XPAN_API).buildUpon()
            .appendQueryParameter("method", "quota")
            .appendQueryParameter("access_token", token).build().toString()
        val req = Request.Builder().url(url).get().build()
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
                ?: throw BaiduPanException("列目录响应异常")
            if (parsed.errno == -9) return@withContext emptyMap()
            if (parsed.list.isEmpty()) break
            for (f in parsed.list) {
                if (f.isDir == 0 && f.path.endsWith(".json")) {
                    val name = f.path.substringAfterLast('/').removeSuffix(".json")
                    result[name.uppercase()] = f.serverMtime
                }
            }
            start += parsed.list.size
        }
        result
    }

    suspend fun downloadData(path: String, token: String): ByteArray = withContext(Dispatchers.IO) {
        val url = Uri.parse(BaiduPanConfig.FILE_API).buildUpon()
            .appendQueryParameter("method", "download")
            .appendQueryParameter("access_token", token)
            .appendQueryParameter("path", path)
            .appendQueryParameter("threshold", "0")
            .build().toString()
        val req = Request.Builder().url(url).get().header("User-Agent", UA).build()
        val resp = client.newCall(req).execute().use { it }
        val body = resp.body?.string() ?: throw BaiduPanException("下载元信息为空")
        val parsed = runCatching { AppStorage.json.decodeFromString<DownloadResponse>(body) }.getOrNull()
            ?: throw BaiduPanException("下载响应异常：${body.take(200)}")
        val dlink = parsed.list.firstOrNull()?.dlink
            ?: throw BaiduPanException("文件不存在：$path")
        val realUrl = Uri.parse(dlink).buildUpon()
            .appendQueryParameter("access_token", token).build().toString()
        val realReq = Request.Builder().url(realUrl).get().header("User-Agent", UA).build()
        client.newCall(realReq).execute().use { r ->
            if (r.code >= 400) throw BaiduPanException("下载失败 HTTP ${r.code}")
            r.body?.bytes() ?: throw BaiduPanException("下载内容为空")
        }
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

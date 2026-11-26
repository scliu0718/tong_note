package com.tong.log.app.ui

import androidx.compose.foundation.Image
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp
import com.tong.log.app.data.model.TodoStatus
import com.tong.log.app.ui.theme.StatusDoing
import com.tong.log.app.ui.theme.StatusDone
import com.tong.log.app.ui.theme.StatusTerminated
import com.tong.log.app.ui.theme.StatusTodo

// 四个任务状态图标（viewBox 1024 的 SVG 填充路径，与 Mac / Windows 同源）
private const val PATH_TODO =
    "M0 512c0 281.6 228.937143 512 512 512s512-228.937143 512-512c0.146286-281.453714-228.864-512-511.853714-512C230.546286 0 0 229.010286 0 512z m719.725714 21.211429L407.771429 713.289143a18.285714 18.285714 0 0 1-27.501715-15.945143V337.261714c0-14.116571 15.286857-22.966857 27.501715-15.945143L719.725714 501.394286a18.505143 18.505143 0 0 1 0 31.890285z"
private const val PATH_DOING =
    "M512 0C229.233778 0 0 229.233778 0 512s229.233778 512 512 512 512-229.233778 512-512S794.766222 0 512 0z m306.048 622.165333H437.347556v-10.311111h-3.825778V231.153778h64.725333v326.300444H818.062222v64.711111z"
private const val PATH_DONE =
    "M512 1024C229.216 1024 0 794.784 0 512S229.216 0 512 0s512 229.216 512 512-229.216 512-512 512z m-49.568-377.152l-146.496-148.224-96.512 92.256c70.208 37.76 168.64 106.816 252.896 213.696 59.52-111.936 243.008-340.896 332.256-361.28-14.4-57.728-22.56-166.016 0-223.872-183.04 120.704-342.144 427.424-342.144 427.424z"
private const val PATH_TERMINATED =
    "M512 64.512q92.16 0 173.568 34.816t142.336 95.744 95.744 142.336 34.816 173.568-34.816 173.568-95.744 142.336-142.336 95.744-173.568 34.816-174.08-34.816-142.336-95.744-95.744-142.336-35.328-173.568 35.328-173.568 95.744-142.336 142.336-95.744 174.08-34.816zM707.584 574.464q29.696 0 49.664-18.432t19.968-47.104q0-29.696-19.968-46.08t-49.664-16.384l-392.192 0q-29.696 0-49.664 16.384t-19.968 46.08q0 28.672 19.968 47.104t49.664 18.432l392.192 0z"

private fun statusVector(path: String, color: Color): ImageVector =
    ImageVector.Builder(
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 1024f,
        viewportHeight = 1024f,
    ).addPath(
        pathData = PathParser().parsePathString(path).toNodes(),
        fill = SolidColor(color),
    ).build()

/** 任务状态图标：待完成=红色播放三角 / 进行中=黄色指针 / 已完成=绿色对勾 / 已终止=灰色减号。 */
@Composable
fun StatusRing(status: String, modifier: Modifier = Modifier) {
    val vector = remember(status) {
        when (status) {
            TodoStatus.DOING -> statusVector(PATH_DOING, StatusDoing)
            TodoStatus.DONE -> statusVector(PATH_DONE, StatusDone)
            TodoStatus.TERMINATED -> statusVector(PATH_TERMINATED, StatusTerminated)
            else -> statusVector(PATH_TODO, StatusTodo)
        }
    }
    Image(imageVector = vector, contentDescription = null, modifier = modifier)
}

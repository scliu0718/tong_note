# 本项目 release 默认关闭混淆（isMinifyEnabled = false），以下规则仅为将来开启时兜底。

# kotlinx.serialization：保留生成的序列化器
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class **$$serializer { *; }
-keepclasseswithmembers class kotlinx.serialization.** { *; }

# OkHttp
-dontwarn okhttp3.**
-dontwarn okio.**

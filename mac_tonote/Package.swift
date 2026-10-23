// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "WorkLog",
    platforms: [
        .macOS(.v14)
    ],
    targets: [
        // 纯 Foundation 层：模型、存储、受控 HTML 编解码、附件仓库（跨平台可复用）
        .target(
            name: "WorkLogCore",
            path: "Sources/WorkLogCore"
        ),
        // macOS 富文本层：NSAttributedString ↔ 富文本文档双向转换与编辑命令
        .target(
            name: "WorkLogRichText",
            dependencies: ["WorkLogCore"],
            path: "Sources/WorkLogRichText"
        ),
        .executableTarget(
            name: "WorkLog",
            dependencies: ["WorkLogCore", "WorkLogRichText"],
            path: "Sources/WorkLog"
        ),
        .testTarget(
            name: "WorkLogCoreTests",
            dependencies: ["WorkLogCore"],
            path: "Tests/WorkLogCoreTests"
        ),
        .testTarget(
            name: "WorkLogRichTextTests",
            dependencies: ["WorkLogRichText", "WorkLogCore"],
            path: "Tests/WorkLogRichTextTests"
        ),
    ]
)

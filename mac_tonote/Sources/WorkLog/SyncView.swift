import SwiftUI

struct SyncView: View {
    @EnvironmentObject private var auth: BaiduPanAuthService
    @Environment(\.dismiss) private var dismiss
    @State private var autoSync = true
    @State private var frequency = "5 分钟"

    var body: some View {
        HStack(spacing: 0) {
            mainContent
            sidePanel
        }
        .background(Theme.windowBg)
        .task { await auth.loadAccount() }
    }

    // MARK: 主区域
    private var mainContent: some View {
        ScrollView(showsIndicators: false) {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("同步与存储")
                        .font(.system(size: 20, weight: .bold))
                        .foregroundColor(Theme.textPrimary)
                    Text("笔记数据存放于你自己的百度网盘，同笔记仅做读写，不另建服务器。")
                        .font(.system(size: 13))
                        .foregroundColor(Theme.textTertiary)
                }

                syncStatusCard
                cloudTreeCard
                accountCard
            }
            .padding(28)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var syncStatusCard: some View {
        card("同步状态") {
            VStack(spacing: 0) {
                row("自动同步", subtitle: "联网时自动增量同步（默认开启）") {
                    Toggle("", isOn: $autoSync)
                        .toggleStyle(.switch)
                        .tint(Theme.accent)
                        .labelsHidden()
                }
                row("同步频率", subtitle: "两次同步的最小间隔") {
                    Text(frequency).font(.system(size: 13, weight: .medium))
                }
                row("最近同步", subtitle: "上次成功同步时间") {
                    Text("14:32:08 · 刚刚").font(.system(size: 13, weight: .medium))
                        .foregroundColor(Theme.accent)
                }
                row("立即同步") {
                    Button(action: {}) {
                        HStack(spacing: 6) {
                            Image(systemName: "arrow.triangle.2.circlepath").font(.system(size: 13))
                            Text("同步全部").font(.system(size: 13, weight: .semibold))
                        }
                        .foregroundColor(.white)
                        .padding(.horizontal, 16).padding(.vertical, 7)
                        .background(Theme.accent)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var cloudTreeCard: some View {
        card("云端目录结构（百度网盘 /apps/tong_log）") {
            VStack(alignment: .leading, spacing: 3) {
                Text("/apps/tong_log/").foregroundColor(Theme.baiduDeep).fontWeight(.semibold)
                Text("  ├─ manifest.json    # 全量索引：笔记本 / 笔记 / 标签 / 修订号")
                Text("  ├─ notes/            # 笔记正文（一笔记一文件）")
                Text("  │   ├─ 1729584921230.md")
                Text("  │   ├─ 1729586304571.md  ← 正在编辑").highlightRow()
                Text("  │   └─ …")
                Text("  ├─ assets/           # 附件（按笔记隔离）")
                Text("  │   └─ 1729586304571/img_0001.png")
                Text("  ├─ trash/            # 回收站")
                Text("  └─ .meta/sync_state.json")
            }
            .font(.system(size: 12.5, design: .monospaced))
            .foregroundColor(Theme.textSecondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .background(Color(hex: 0xFAFAFB))
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
    }

    private var accountCard: some View {
        card("账号与安全") {
            VStack(spacing: 0) {
                row("百度网盘账号") {
                    Text(auth.userInfo?.displayName ?? "已授权").font(.system(size: 13, weight: .medium))
                }
                row("授权方式", subtitle: "百度网盘开放平台 OAuth 2.0") {
                    Button("重新授权") {}
                        .buttonStyle(.plain)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundColor(Theme.textSecondary)
                        .padding(.horizontal, 14).padding(.vertical, 6)
                        .background(Color.white)
                        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line, lineWidth: 1))
                }
                row("退出登录") {
                    Button("退出") {
                        dismiss()
                        auth.signOut()
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundColor(Theme.red)
                    .padding(.horizontal, 14).padding(.vertical, 6)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color(hex: 0xF3D2D3), lineWidth: 1))
                }
            }
        }
    }

    // MARK: 右侧面板
    private var sidePanel: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("存储空间")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Theme.textQuaternary)
            usageCard
            Text("数据说明")
                .font(.system(size: 11, weight: .semibold))
                .foregroundColor(Theme.textQuaternary)
                .padding(.top, 4)
            Text("所有笔记为开放格式，可直接用任意文本编辑器打开，也可通过百度网盘 App 查看、导出或迁移，数据完全归你所有。")
                .font(.system(size: 12))
                .foregroundColor(Theme.textSecondary)
                .lineSpacing(4)
                .padding(16)
                .background(Color.white)
                .clipShape(RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.line, lineWidth: 1))
            Spacer()
            Text("同笔记 · 数据属于你自己\n无需订阅 · 无服务器存储")
                .font(.system(size: 11))
                .foregroundColor(Theme.textQuaternary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
                .lineSpacing(3)
        }
        .padding(20)
        .frame(width: 280)
        .background(Theme.sidebarBg)
    }

    private var usageCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            (Text(formatBytes(auth.quota?.used)).font(.system(size: 26, weight: .bold)) +
             Text(" / \(formatBytes(auth.quota?.total))").font(.system(size: 12)).foregroundColor(Theme.textQuaternary))
                .foregroundColor(Theme.textPrimary)
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 3).fill(Color(hex: 0xE5E5E8))
                    RoundedRectangle(cornerRadius: 3)
                        .fill(Theme.baidu)
                        .frame(width: geo.size.width * quotaRatio)
                }
            }
            .frame(height: 6).padding(.vertical, 12)
            Text("已用空间 · 以百度网盘为准\n数据完全归你所有，可随时导出迁移。")
                .font(.system(size: 11))
                .foregroundColor(Theme.textQuaternary)
                .lineSpacing(4)
        }
        .padding(16)
        .background(Color.white)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.line, lineWidth: 1))
    }

    private func formatBytes(_ bytes: Int64?) -> String {
        guard let b = bytes, b >= 0 else { return "—" }
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        return formatter.string(fromByteCount: b)
    }

    private var quotaRatio: Double {
        guard let total = auth.quota?.total, let used = auth.quota?.used, total > 0 else { return 0 }
        return min(max(Double(used) / Double(total), 0), 1)
    }

    // MARK: 通用组件
    private func card<Content: View>(_ title: String,
                                     @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "doc.text")
                    .font(.system(size: 15))
                    .foregroundColor(Theme.accent)
                Text(title)
                    .font(.system(size: 13, weight: .bold))
                    .foregroundColor(Theme.textPrimary)
            }
            .padding(.bottom, 10)
            content()
        }
        .padding(18)
        .background(Color.white)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.line, lineWidth: 1))
    }

    private func row<Accessory: View>(_ label: String,
                                      subtitle: String? = nil,
                                      @ViewBuilder accessory: () -> Accessory) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(.system(size: 13)).foregroundColor(Theme.textSecondary)
                if let subtitle {
                    Text(subtitle).font(.system(size: 11)).foregroundColor(Theme.textQuaternary)
                }
            }
            Spacer()
            accessory()
        }
        .padding(.vertical, 9)
        .overlay(Rectangle().fill(Theme.lineSoft).frame(height: 1), alignment: .bottom)
    }
}

private extension Text {
    func highlightRow() -> Text {
        self.foregroundColor(Color(hex: 0x8A5A00))
    }
}

import SwiftUI

struct LoginView: View {
    @EnvironmentObject private var auth: BaiduPanAuthService

    var body: some View {
        HStack(spacing: 0) {
            brandPanel
            loginPanel
        }
        .background(Theme.windowBg)
    }

    // MARK: - 左品牌区
    private var brandPanel: some View {
        ZStack {
            LinearGradient(
                colors: [Color(hex: 0x17C26B), Color(hex: 0x0F9D52), Color(hex: 0x0C7A40)],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
            Circle().fill(Color.white.opacity(0.08))
                .frame(width: 420, height: 420)
                .offset(x: 240, y: -280)
            Circle().fill(Color.white.opacity(0.06))
                .frame(width: 380, height: 380)
                .offset(x: 280, y: 340)

            VStack(alignment: .leading, spacing: 0) {
                brandRow
                Spacer()
                pitchBlock
                Spacer()
                footerText
            }
            .padding(52)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var brandRow: some View {
        HStack(spacing: 14) {
            Image(systemName: "square.and.pencil")
                .font(.system(size: 26, weight: .medium))
                .foregroundColor(.white)
                .frame(width: 52, height: 52)
                .background(Color.white.opacity(0.16))
                .clipShape(RoundedRectangle(cornerRadius: 13))
                .overlay(RoundedRectangle(cornerRadius: 13).stroke(Color.white.opacity(0.22), lineWidth: 1))
            VStack(alignment: .leading, spacing: 1) {
                Text("同笔记").font(.system(size: 22, weight: .bold)).foregroundColor(.white)
                Text("记录 · 同步 · 属于你").font(.system(size: 12)).foregroundColor(.white.opacity(0.8))
            }
        }
    }

    private var pitchBlock: some View {
        VStack(alignment: .leading, spacing: 28) {
            VStack(alignment: .leading, spacing: 16) {
                Text("你的每一份记录，\n都交给你自己保管。")
                    .font(.system(size: 32, weight: .bold))
                    .foregroundColor(.white)
                    .lineSpacing(4)
                Text("不用订阅、不锁数据。笔记直接写入你自己的百度网盘，随时可导出、可迁移，从此告别「笔记困在 App 里」的焦虑。")
                    .font(.system(size: 14))
                    .foregroundColor(.white.opacity(0.9))
                    .lineSpacing(5)
                    .frame(maxWidth: 400, alignment: .leading)
            }
            VStack(alignment: .leading, spacing: 12) {
                featureRow("数据存储于百度网盘，跨 Mac / Windows / iOS / Android 同步")
                featureRow("笔记、表格、任务和附件，格式开放可迁移")
                featureRow("离线编辑，联网自动增量同步")
            }
        }
    }

    private func featureRow(_ text: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "checkmark")
                .font(.system(size: 10, weight: .bold))
                .foregroundColor(.white)
                .frame(width: 20, height: 20)
                .background(Color.white.opacity(0.18))
                .clipShape(Circle())
            Text(text).font(.system(size: 13)).foregroundColor(.white.opacity(0.92))
        }
    }

    private var footerText: some View {
        Text("同笔记不会读取你网盘中 worklog 目录以外的任何文件")
            .font(.system(size: 11))
            .foregroundColor(.white.opacity(0.65))
    }

    // MARK: - 右登录区
    private var loginPanel: some View {
        VStack(spacing: 22) {
            Text("欢迎使用同笔记")
                .font(.system(size: 18, weight: .bold))
                .foregroundColor(Theme.textPrimary)
            Text("使用百度网盘账号登录，笔记将存储于你的网盘")
                .font(.system(size: 13))
                .foregroundColor(Theme.textTertiary)
                .padding(.top, -14)

            Button(action: { auth.signIn() }) {
                HStack(spacing: 10) {
                    if auth.isAuthorizing {
                        ProgressView().controlSize(.small).tint(.white)
                    } else {
                        Image(systemName: "cloud.fill")
                            .font(.system(size: 18))
                    }
                    Text(auth.isAuthorizing ? "正在授权…" : "使用百度网盘账号登录")
                        .font(.system(size: 15, weight: .semibold))
                }
                .foregroundColor(.white)
                .frame(maxWidth: 340)
                .frame(height: 48)
                .background(Theme.baidu.opacity(auth.isAuthorizing ? 0.6 : 1))
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .shadow(color: Theme.baidu.opacity(0.28), radius: 7, y: 3)
            }
            .buttonStyle(.plain)
            .disabled(auth.isAuthorizing)

            if let msg = auth.bannerMessage {
                Text(msg)
                    .font(.system(size: 12))
                    .foregroundColor(Theme.red)
                    .multilineTextAlignment(.center)
                    .lineSpacing(3)
                    .frame(maxWidth: 340)
            }

            HStack(spacing: 12) {
                Rectangle().fill(Theme.line).frame(height: 1)
                Text("或").font(.system(size: 12)).foregroundColor(Theme.textQuaternary)
                Rectangle().fill(Theme.line).frame(height: 1)
            }
            .frame(maxWidth: 340)

            VStack(spacing: 8) {
                Button(action: { auth.signInViaBrowser() }) {
                    HStack(spacing: 8) {
                        if auth.isAuthorizing {
                            ProgressView().controlSize(.small).tint(Theme.baidu)
                        } else {
                            Image(systemName: "qrcode")
                                .font(.system(size: 16))
                        }
                        Text("浏览器扫码登录")
                            .font(.system(size: 13, weight: .semibold))
                    }
                    .foregroundColor(Theme.baidu)
                    .frame(maxWidth: 340)
                    .frame(height: 40)
                    .background(Color.white)
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.baidu.opacity(0.5), lineWidth: 1))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain)
                .disabled(auth.isAuthorizing)

                Text("跳转浏览器后切换到「扫码登录」，用百度 / 百度网盘 App 扫一扫，\n授权完成后自动回到同笔记。")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.textQuaternary)
                    .multilineTextAlignment(.center)
                    .lineSpacing(3)
            }
            .frame(maxWidth: 340)

            Text("登录即代表你授权同笔记通过百度网盘开放平台读写\n/apps/tong_log 目录。授权信息仅保存在本机。")
                .font(.system(size: 11))
                .foregroundColor(Theme.textQuaternary)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
                .frame(maxWidth: 340)

            Button(action: { UserGuideWindow.show() }) {
                HStack(spacing: 5) {
                    Image(systemName: "questionmark.circle")
                        .font(.system(size: 12, weight: .medium))
                    Text("查看使用说明")
                        .font(.system(size: 12, weight: .medium))
                }
                .foregroundColor(Theme.textTertiary)
            }
            .buttonStyle(.plain)
            .help("打开使用说明")
            .padding(.top, 4)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

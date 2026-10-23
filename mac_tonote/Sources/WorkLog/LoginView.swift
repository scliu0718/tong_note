import SwiftUI

struct LoginView: View {
    var onLogin: () -> Void

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
                Text("WorkLog").font(.system(size: 22, weight: .bold)).foregroundColor(.white)
                Text("工作日志 · 笔记").font(.system(size: 12)).foregroundColor(.white.opacity(0.8))
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
                featureRow("数据存于百度网盘，跨 Mac / Android 同步")
                featureRow("Markdown 笔记 + 附件，格式开放可迁移")
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
        Text("WorkLog 不会读取你网盘中 worklog 目录以外的任何文件")
            .font(.system(size: 11))
            .foregroundColor(.white.opacity(0.65))
    }

    // MARK: - 右登录区
    private var loginPanel: some View {
        VStack(spacing: 22) {
            Text("欢迎使用 WorkLog")
                .font(.system(size: 18, weight: .bold))
                .foregroundColor(Theme.textPrimary)
            Text("使用百度网盘账号登录，笔记将存储于你的网盘")
                .font(.system(size: 13))
                .foregroundColor(Theme.textTertiary)
                .padding(.top, -14)

            Button(action: onLogin) {
                HStack(spacing: 10) {
                    Image(systemName: "cloud.fill")
                        .font(.system(size: 18))
                    Text("使用百度网盘账号登录")
                        .font(.system(size: 15, weight: .semibold))
                }
                .foregroundColor(.white)
                .frame(maxWidth: 340)
                .frame(height: 48)
                .background(Theme.baidu)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .shadow(color: Theme.baidu.opacity(0.28), radius: 7, y: 3)
            }
            .buttonStyle(.plain)

            HStack(spacing: 12) {
                Rectangle().fill(Theme.line).frame(height: 1)
                Text("或").font(.system(size: 12)).foregroundColor(Theme.textQuaternary)
                Rectangle().fill(Theme.line).frame(height: 1)
            }
            .frame(maxWidth: 340)

            VStack(spacing: 8) {
                // 扫码占位（真机二维码后续接入）
                RoundedRectangle(cornerRadius: 6)
                    .fill(
                        ImagePaint(image: Image(systemName: "qrcode"))
                    )
                    .frame(width: 92, height: 92)
                    .foregroundColor(Theme.textPrimary.opacity(0.8))
                Text("打开「百度网盘 App」扫码登录")
                    .font(.system(size: 12))
                    .foregroundColor(Theme.textTertiary)
            }
            .frame(maxWidth: 340)
            .frame(height: 150)
            .overlay(
                RoundedRectangle(cornerRadius: 10)
                    .stroke(Theme.line, style: StrokeStyle(lineWidth: 1, dash: [4, 4]))
            )

            Text("登录即代表你授权 WorkLog 通过百度网盘开放平台读写\n/apps/WorkLog 目录。授权信息仅保存在本机密钥链中。")
                .font(.system(size: 11))
                .foregroundColor(Theme.textQuaternary)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
                .frame(maxWidth: 340)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

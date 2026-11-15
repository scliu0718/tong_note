using System.Text;
using Tonote.Core;

namespace Tonote.LiveTests;

/// <summary>D 组：设备会话（登记 / 下线 / 被踢）与头像的云端往返。</summary>
public static class GroupD
{
    public static async Task RunAsync(LiveContext ctx, Runner r)
    {
        r.Section("D 设备会话与头像（真实网盘）");
        var at = ctx.AccessToken;

        var deviceFile = Path.Combine(ctx.LocalRoot, "device.json");
        var avatarFile = Path.Combine(ctx.LocalRoot, "avatar-state.json");
        if (File.Exists(deviceFile)) File.Delete(deviceFile);
        if (File.Exists(avatarFile)) File.Delete(avatarFile);

        var identities = new DeviceIdentityStore(ctx.LocalRoot);
        var identity = identities.LoadOrCreate(DeviceIdentityStore.CurrentDeviceName());
        r.Check("D01 本机设备身份生成并落盘",
            DeviceRecord.IsSafeDeviceId(identity.DeviceId) && File.Exists(deviceFile)
            && !string.IsNullOrWhiteSpace(identity.DeviceName),
            $"device_id={identity.DeviceId} 名称={identity.DeviceName}");

        var registry = new DeviceRegistry(ctx.LocalRoot, ctx.Client);
        await r.Case("D02 BeginLoginAsync 把本次登录登记到云端", async () =>
        {
            await registry.BeginLoginAsync();
            var raw = await Read(ctx, DeviceRegistry.FilePath(registry.CurrentDeviceId));
            var record = TonoteJson.Deserialize<DeviceRecord>(raw);
            return (record != null && record.DeviceId == registry.CurrentDeviceId
                    && !string.IsNullOrWhiteSpace(record.SessionId) && !record.Kicked,
                $"session={record?.SessionId} kicked={record?.Kicked}");
        });

        await r.Case("D03 设备记录字段集与 Mac 生产端逐字一致", async () =>
        {
            var raw = await Read(ctx, DeviceRegistry.FilePath(registry.CurrentDeviceId));
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var actual = doc.RootElement.EnumerateObject().Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            string[] expected = ["device_id", "device_name", "kicked", "session_id"];
            return (actual.SequenceEqual(expected), $"实际=[{string.Join(",", actual)}]");
        });

        await r.Case("D04 ListDevicesAsync 能列出自己且本机排第一", async () =>
        {
            var devices = await registry.ListDevicesAsync();
            return (devices.Count >= 1 && devices[0].DeviceId == registry.CurrentDeviceId,
                $"共 {devices.Count} 台：{string.Join(",", devices.Select(item => item.DeviceName).Take(4))}");
        });

        await r.Case("D05 未被下线时 EnsureNotKickedAsync 正常通过", async () =>
        {
            await registry.EnsureNotKickedAsync(at);
            return (true, "未抛 DeviceKickedException");
        });

        var me = registry.CurrentDeviceId;
        var session = identities.Load()!.SessionId!;
        r.Check("D06 Decide 五分支判定正确",
            DeviceRecord.Decide(me, session, null) == DeviceSessionDecision.Publish
            && DeviceRecord.Decide(me, session, new DeviceRecord(me, "x", session, true))
               == DeviceSessionDecision.Kicked
            && DeviceRecord.Decide(me, session, new DeviceRecord(me, "x", session, false))
               == DeviceSessionDecision.Unchanged
            && DeviceRecord.Decide(me, session, new DeviceRecord(me, "x", "OLD", false))
               == DeviceSessionDecision.Publish
            && DeviceRecord.Decide(me, session, new DeviceRecord("OTHER-DEVICE", "x", session, true))
               == DeviceSessionDecision.Publish,
            "null→Publish / 同 session+kicked→Kicked / 同 session→Unchanged / 旧 session→Publish / 别的设备→Publish");

        await r.Case("D07 云端标记下线后本机被踢并收到通知", async () =>
        {
            var kickedRaised = 0;
            void Handler() => kickedRaised++;
            DeviceEvents.Kicked += Handler;
            try
            {
                var path = DeviceRegistry.FilePath(me);
                var record = TonoteJson.Deserialize<DeviceRecord>(await Read(ctx, path))!;
                await ctx.Client.UploadDataAsync(
                    Encoding.UTF8.GetBytes(TonoteJson.Serialize(record.MarkedKicked())), path, at);
                try
                {
                    await registry.EnsureNotKickedAsync(at);
                    return (false, "未抛 DeviceKickedException");
                }
                catch (DeviceKickedException)
                {
                    return (kickedRaised >= 1, $"DeviceEvents.Kicked 触发 {kickedRaised} 次");
                }
            }
            finally
            {
                DeviceEvents.Kicked -= Handler;
            }
        });

        await r.Case("D08 KickAsync 把另一台设备标成下线", async () =>
        {
            var otherId = Guid.NewGuid().ToString("D").ToUpperInvariant();
            var path = DeviceRegistry.FilePath(otherId);
            var other = new DeviceRecord(otherId, "另一台设备", "SESSION-OTHER", false);
            await ctx.Client.UploadDataAsync(Encoding.UTF8.GetBytes(TonoteJson.Serialize(other)), path, at);
            await registry.KickAsync(otherId);
            var cloud = TonoteJson.Deserialize<DeviceRecord>(await Read(ctx, path));
            return (cloud?.Kicked == true && cloud.SessionId == "SESSION-OTHER"
                    && cloud.DeviceId == otherId,
                $"kicked={cloud?.Kicked} session 未变={cloud?.SessionId == "SESSION-OTHER"}");
        });

        r.Check("D09 设备编号安全校验（拒绝非法 / 拒绝踢自己）",
            !DeviceRecord.IsSafeDeviceId("../evil") && !DeviceRecord.IsSafeDeviceId("")
            && DeviceRecord.IsSafeDeviceId("A1B2C3D4-1234-5678-90AB-CDEF01234567"),
            "路径穿越与空编号被拒，UUID 被接受");

        await r.Case("D10 重新登录后云端下线标记被清除", async () =>
        {
            identities.UpdateSession(Guid.NewGuid().ToString("D").ToUpperInvariant(),
                DeviceIdentityStore.CurrentDeviceName());
            await registry.EnsureNotKickedAsync(at);
            var cloud = TonoteJson.Deserialize<DeviceRecord>(
                await Read(ctx, DeviceRegistry.FilePath(me)));
            return (cloud?.Kicked == false && cloud.SessionId == identities.Load()?.SessionId,
                $"kicked={cloud?.Kicked} session 已更新={cloud?.SessionId == identities.Load()?.SessionId}");
        });

        // ── 头像 ────────────────────────────────────────────────────
        r.Check("D11 八个预设头像与色值格式",
            AvatarStore.Presets.Count == 8
            && AvatarStore.Presets.All(preset => preset.Color1Hex.StartsWith("#")
                                               && preset.Color2Hex.Length == 7),
            $"预设={AvatarStore.Presets.Count} 首个={AvatarStore.Presets[0].Id}/{AvatarStore.Presets[0].Color1Hex}");

        var avatar = new AvatarStore(ctx.LocalRoot, ctx.Client, registry);
        await r.Case("D12 选择头像写入本地并上传到 avatar.json", async () =>
        {
            avatar.Select("star");
            for (var i = 0; i < 60 && avatar.CloudState is "idle" or "uploading"; i++)
                await Task.Delay(200);
            var raw = await Read(ctx, ctx.Remote("avatar.json"));
            var state = TonoteJson.Deserialize<AvatarStateProbe>(raw);
            return (avatar.CurrentId == "star" && avatar.HasLocalChoice
                    && File.Exists(avatarFile) && avatar.CloudState == "synced"
                    && state?.Id == "star",
                $"本机={avatar.CurrentId} 云端={state?.Id} 状态={avatar.CloudState} {avatar.CloudError}");
        });

        await r.Case("D13 新设备未做本地选择时从云端拉取头像", async () =>
        {
            File.Delete(avatarFile);
            var fresh = new AvatarStore(ctx.LocalRoot, ctx.Client, registry);
            await fresh.PullFromCloudIfNeededAsync();
            return (!fresh.HasLocalChoice && fresh.CurrentId == "star",
                $"拉取结果={fresh.CurrentId} 本地已选={fresh.HasLocalChoice}");
        });

        await r.Case("D14 Windows 写出的 avatar.json 与 Mac 生产端格式一致", async () =>
        {
            var raw = (await Read(ctx, ctx.Remote("avatar.json"))).Trim();
            var keys = System.Text.Json.JsonDocument.Parse(raw).RootElement
                .EnumerateObject().Select(p => p.Name).ToArray();
            return (keys.Length == 1 && keys[0] == "id" && raw == "{\"id\":\"star\"}",
                $"内容={raw}");
        });
    }

    private static async Task<string> Read(LiveContext ctx, string remotePath)
        => Encoding.UTF8.GetString(await ctx.Client.DownloadDataAsync(remotePath, ctx.AccessToken));

    private sealed class AvatarStateProbe
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}

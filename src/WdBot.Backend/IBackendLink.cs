namespace WdBot.Backend;

/// <summary>處理微服務下發的指令。實作應盡快返回，耗時工作自行排入佇列。</summary>
public interface IBackendHandler
{
    /// <summary>全量設定：清單外的機器人要停止。</summary>
    Task OnConfigSnapshotAsync(IReadOnlyList<BotConfig> configs, CancellationToken ct);

    Task OnConfigUpsertAsync(BotConfig config, CancellationToken ct);

    Task OnConfigRemoveAsync(string botId, CancellationToken ct);

    Task OnSendMessageAsync(SendMessageCommand command, CancellationToken ct);
}

/// <summary>
/// 與 BOT 管理後台微服務的連線。傳輸方式（WebSocket、HTTP、MQ）由實作決定，
/// 機器人邏輯只依賴這個介面。
/// </summary>
public interface IBackendLink
{
    /// <summary>維持連線並把下行指令交給 handler，直到取消。</summary>
    Task RunAsync(IBackendHandler handler, CancellationToken ct);

    /// <summary>以下上報方法都只放進送出佇列，不會因微服務斷線而阻塞。</summary>
    void ReportIncomingMessage(IncomingMessageReport report);

    void ReportSendResult(SendResultReport report);

    void ReportChannelSnapshot(ChannelSnapshotReport report);

    void ReportChannelMembersChanged(ChannelMembersChangedReport report);

    /// <summary>狀態只保留每個機器人的最新一筆；重連後會重送所有機器人的最新狀態。</summary>
    void ReportBotStatus(BotStatusReport report);
}

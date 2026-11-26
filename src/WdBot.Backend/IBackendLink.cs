namespace WdBot.Backend;

/// <summary>處理微服務下發的指令。實作應盡快返回，耗時工作自行排入佇列。</summary>
public interface IBackendHandler
{
    /// <summary>全量設定：清單外的機器人要停止。</summary>
    /// <param name="configs">微服務下發的所有機器人設定。</param>
    /// <param name="ct">用來取消作業。</param>
    Task OnConfigSnapshotAsync(IReadOnlyList<BotConfig> configs, CancellationToken ct);

    /// <summary>新增或更新單一機器人設定：不存在就啟動，帳號相關欄位變更就重新登入，其他欄位直接套用。</summary>
    /// <param name="config">要新增或更新的機器人設定。</param>
    /// <param name="ct">用來取消作業。</param>
    Task OnConfigUpsertAsync(BotConfig config, CancellationToken ct);

    /// <summary>停止並移除指定的機器人。</summary>
    /// <param name="botId">要移除的機器人 ID（bot_id）。</param>
    /// <param name="ct">用來取消作業。</param>
    Task OnConfigRemoveAsync(string botId, CancellationToken ct);

    /// <summary>要求指定的機器人私訊玩家；結果以 <see cref="IBackendLink.ReportSendResult"/> 回報。</summary>
    /// <param name="command">微服務下發的私訊指令。</param>
    /// <param name="ct">用來取消作業。</param>
    Task OnSendMessageAsync(SendMessageCommand command, CancellationToken ct);
}

/// <summary>
/// 與 BOT 管理後台微服務的連線。傳輸方式（WebSocket、HTTP、MQ）由實作決定，
/// 機器人邏輯只依賴這個介面。
/// </summary>
public interface IBackendLink
{
    /// <summary>維持連線並把下行指令交給 handler，直到取消。</summary>
    /// <param name="handler">處理下行指令的對象，通常是 BotManager。</param>
    /// <param name="ct">取消時斷線並結束。</param>
    Task RunAsync(IBackendHandler handler, CancellationToken ct);

    /// <summary>以下上報方法都只放進送出佇列，不會因微服務斷線而阻塞。</summary>
    /// <param name="report">收到的玩家私訊。</param>
    void ReportIncomingMessage(IncomingMessageReport report);

    /// <summary>回報私訊指令的執行結果。</summary>
    /// <param name="report">私訊指令的執行結果。</param>
    void ReportSendResult(SendResultReport report);

    /// <summary>回報機器人所在公頻的完整線上名單。</summary>
    /// <param name="report">公頻的完整線上名單。</param>
    void ReportChannelSnapshot(ChannelSnapshotReport report);

    /// <summary>回報公頻成員進出。</summary>
    /// <param name="report">公頻成員進出的內容。</param>
    void ReportChannelMembersChanged(ChannelMembersChangedReport report);

    /// <summary>狀態只保留每個機器人的最新一筆；重連後會重送所有機器人的最新狀態。</summary>
    /// <param name="report">機器人目前的狀態。</param>
    void ReportBotStatus(BotStatusReport report);
}

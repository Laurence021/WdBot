# WD 機器人服務

這個程式從 BOT 管理後台微服務取得機器人設定，自動登入 WD 遊戲，並維持聊天 WS 連線。玩家私訊機器人時，程式轉給微服務；微服務回覆後，程式再私訊回該玩家。

- 遊戲端規格：[WD機器人串接功能與API規格_20261112.md](WD機器人串接功能與API規格_20261112.md)
- 微服務協定：[docs/backend-protocol.md](docs/backend-protocol.md)

## 專案結構

| 專案 | 說明 |
|---|---|
| `src/WdBot.Game` | 遊戲端 client：App API、token 管理、聊天 WS、私訊送出佇列 |
| `src/WdBot.Backend` | 微服務介面 `IBackendLink` 與 WebSocket 實作 |
| `src/WdBot.Service` | 主程式（.NET Worker Service）：`BotManager` 管理多個 `BotInstance` |

## 設定（`src/WdBot.Service/appsettings.json`）

| 區段 | 說明 |
|---|---|
| `Backend` | 微服務 WebSocket 網址、API key、服務實例 ID |
| `Game:Environments` | 正式站與測試站的 API 和聊天 WS 網址 |
| `Game:RequestHeaders`、`Game:UnityVersion` | 送給遊戲方的 Header，讓請求看起來像 Unity 做的安卓 App（見下方說明） |
| `Bot` | token 刷新間隔、重連與逾時參數 |

機器人帳號由微服務下發，不寫在設定檔中。

### 送給遊戲方的 Header

.NET 預設不送 User-Agent，遊戲方很容易分辨。`Game:RequestHeaders` 設定每個請求帶上的 Header，預設模擬 Unity（UnityWebRequest）做的安卓 App：

```
User-Agent: UnityPlayer/{unity_version} (UnityWebRequest/1.0, libcurl/8.5.0-DEV)
X-Unity-Version: {unity_version}
Accept: */*
Accept-Encoding: deflate, gzip
Content-Type: application/json
```

- `Http` 用於 App API，`WebSocket` 用於聊天 WS 的連線請求（預設帶相同的 User-Agent 與 X-Unity-Version）
- 可用變數：
  - `{unity_version}`：來自 `Game:UnityVersion`
  - `{app_version}`、`{os}`、`{device_id}`：來自機器人設定
  - `{android_version}`：從 os 取出的數字，例如 Android14 → 14
  - `{phone_model}`：機器人的 `phone_model`，沒填時用 `Game:DefaultPhoneModel`
- 值設成空字串表示不送這個 Header；每個機器人可以在 BOT 設定的 `headers` 覆寫同名 Header
- **Unity 版本與 libcurl 版本要跟真的 App 一樣**：預設值只是常見的版本，請用手機抓真實 App 的封包比對，再修正 `Game:UnityVersion` 與 User-Agent
- 請求內容一律帶 `Content-Length`，不用 chunked 傳送，跟手機 App 相同
- 限制：Header 的順序與 TLS 連線特徵（JA3 指紋）無法做到與 Unity 完全相同

## 建置與執行

```powershell
dotnet build WdBot.slnx
dotnet run --project src/WdBot.Service
```

`dotnet run` 預設以 Development 環境執行，會進入下一節的本機測試模式。正式環境（`DOTNET_ENVIRONMENT` 未設定或為 Production）才會連微服務。

## 本機測試（不需要後台）

後台還沒好之前，可以用本機 JSON 檔提供機器人設定，直接測試登入、進入公頻與接收私訊。所有狀態與收到的訊息都會印在主控台。

1. 在 `src/WdBot.Service` 中，把 `bots.local.example.json` 複製成 `bots.local.json`
2. 填入測試站帳號：

   | 欄位 | 說明 |
   |---|---|
   | `account`、`password` | 測試站的遊戲帳號密碼 |
   | `device_id` | 建議用 UUID，跟一般手機 App 相同，可用 PowerShell `[guid]::NewGuid()` 產生；Android（`device_model` 1）用小寫，iOS（`device_model` 2）用大寫。每個機器人固定一組，不要共用，也不要直接沿用範例的值；設定後不要再改（token 綁定 device_id，換了要重新登入，也會計入註冊次數） |
   | `app_version` | 遊戲目前的版本號（向遊戲方確認） |
   | `phone_model` | 選填，手機型號；只有 Header 設定用到 `{phone_model}` 時才有作用（Unity 預設的 Header 不含手機型號） |
   | `scan_channel_players` | true 時會印出公頻名單與成員進出 |

3. 執行：

   ```powershell
   dotnet run --project src/WdBot.Service
   ```

4. 依 log 確認：

   | 依序會看到 | 代表 |
   |---|---|
   | `啟動機器人 …，使用 test 環境` | 這個機器人使用的環境與登入 API（由 `env` 決定） |
   | `帳號 … 登入成功，暱稱=… player_id=… 等級=…` | 登入成功，並顯示遊戲內暱稱 |
   | `聊天 WS 已連線：wss://…` | 聊天連線建立，並顯示實際連線的網址 |
   | `進入公頻 3(公頻 3)，在線 120 人` | 已進入公頻 |
   | `狀態 Online｜… channel_id=3` | 機器人上線 |
   | `公頻 3(公頻 3) 名單取得完成：2 頁、150 位玩家` | 推送的第一頁之後還有頁面，主動翻完（超過 100 人時才會出現） |
   | `公頻 3(公頻 3) 名單更新（遊戲方推送）：目前在線 120 人` | 機器人自己維護的名單已建立；括號內為來源：遊戲方推送、遊戲方推送＋後續頁、主動掃描 |
   | `公頻 3 已 10 分鐘沒收到遊戲方推送的成員列表，主動掃描一次` | 超過 `Bot:ChannelRescanMinutes` 沒收到 opcode 9 推送 |
   | `公頻 3 異動：進入 2、離開 1，目前在線 121 人` | 玩家進出後的最新人數 |

5. 按 `Ctrl+C` 結束

- 服務執行中修改 `bots.local.json` 並存檔，約 1 秒內就會套用，例如 `enabled` 改成 false 就會停止
- 等級 0（沒綁手機）的帳號也能登入、進公頻、收私訊，只是不能發言
- 測試期間不要用手機登入同一個帳號：重新登入會讓前一組 token 失效，兩條聊天連線也會互相踢線（7000）
- `bots.local.json` 含有密碼，不要提交到版本控制或分享給別人

## Log

| 內容 | 位置 | 何時寫入 |
|---|---|---|
| 一般 log（狀態、登入、連線、錯誤） | 主控台，以及 `logs/wdbot-yyyyMMdd.log` | 一律寫入，等級由 `Logging:LogLevel:Default` 決定（預設 Information） |
| 收到的私訊內容 | `logs/messages/{bot_id}/messages-yyyyMMdd.log`，每個機器人一個資料夾 | 只有 `Logging:LogLevel:WdBot.Messages.Private` 設為 `Debug` 時才寫 |
| 公頻訊息內容 | `logs/messages/{bot_id}/channel-yyyyMMdd.log`，與私訊同一個資料夾 | 只有 `Logging:LogLevel:WdBot.Messages.Channel` 設為 `Debug` 時才寫；只記錄機器人已佔用的公頻 |
| 程式沒有處理的聊天 WS 推送 | `logs/messages/{bot_id}/unhandled-yyyyMMdd.log`，與私訊同一個資料夾；每行記錄 opcode、success、err_code、err_msg、trace_id、server_time、server_name 與 data 原文 | 一律寫入（`Logging:LogLevel:WdBot.Messages.Unhandled` 預設 Information，設為 `None` 可關閉）。包含沒有對應處理的 opcode（例如 1、6），以及帶錯誤的推送 |
| 目前在線名單 | `logs/roster/{env}/channel-{大廳編號}.json`（例如 `logs/roster/prod/channel-3.json`），隨玩家進出更新（最多每 3 秒一次） | 預設開啟（`Bot:WriteRosterFile`）；離開公頻或換廳時刪除舊大廳的檔案，服務啟動時清掉上次留下的檔案 |

- `logs` 資料夾的位置：用 `dotnet run` 時在 `src/WdBot.Service/logs`；發佈或裝成 Windows 服務後，在執行檔所在的資料夾
- log 使用 NLog：輸出位置、格式與保留天數寫在 [nlog.config](src/WdBot.Service/nlog.config)；記錄等級在 appsettings 的 `Logging:LogLevel`；資料夾在 appsettings 的 `FileLogging`
- 主控台輸出比照 Serilog，依參數型別上色：字串青色、數字洋紅色、true/false 與 null 藍色、其他型別綠色；等級標籤 WRN 黃色、ERR 紅底（[TemplateColoredConsoleTarget.cs](src/WdBot.Service/TemplateColoredConsoleTarget.cs)）。輸出被導向檔案時不上色
- 每天一個檔案，各保留 30 天（nlog.config 的 `maxArchiveDays`）
- 私訊與公頻的訊息內容都不會寫進一般 log，也不會顯示在主控台；一般 log 只記錄「收到誰的私訊、message_id」
- 本機測試（Development）預設開啟私訊與公頻內容 log；正式環境預設關閉。私訊（`WdBot.Messages.Private`）與公頻（`WdBot.Messages.Channel`）可以分開開關，要開啟的改成 `Debug`
- 密碼與 token 不會寫進任何 log

## 安裝成 Windows 服務

```powershell
dotnet publish src/WdBot.Service -c Release -o C:\WdBot
sc.exe create WdBot binPath= "C:\WdBot\WdBot.Service.exe" start= auto
```

## 更換微服務的傳輸方式

機器人邏輯只依賴 `IBackendLink`（[src/WdBot.Backend/IBackendLink.cs](src/WdBot.Backend/IBackendLink.cs)）。如果要改用 HTTP 或 MQ：

1. 實作新的 `IBackendLink`
2. 替換 [src/WdBot.Service/Program.cs](src/WdBot.Service/Program.cs) 中的註冊

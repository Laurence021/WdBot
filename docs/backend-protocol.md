# WD 機器人服務 ↔ BOT 管理後台微服務 協定規格（草案 v1）

本文件定義機器人服務程式（以下簡稱「機器人服務」）與 BOT 管理後台微服務（以下簡稱「微服務」）之間的介面。遊戲端的 API 見《WD 機器人串接功能與 API 規格》，本文件不重複。

## 角色分工

| 角色 | 負責 |
|---|---|
| 微服務 | 管理機器人設定；接收玩家私訊；決定回覆內容；要求機器人私訊玩家 |
| 機器人服務 | 依設定登入遊戲、維持 token 與聊天連線；轉送玩家私訊；依指令送出私訊並回報結果 |

## 連線

- 由**機器人服務主動連線**到微服務的 WebSocket 端點，微服務不需要連入機器人服務
- 網址由機器人服務設定 `Backend:Url` 決定，例如 `wss://bot-admin.example.com/ws/bot-service`
- 連線時帶的 Header：

| Header | 說明 |
|---|---|
| `X-Api-Key` | 驗證用金鑰，微服務驗證失敗時直接拒絕升級連線 |
| `X-Service-Instance-Id` | 機器人服務實例 ID，例如 `wdbot-01` |

- 斷線後機器人服務會自動重連（1 秒起，指數退避，上限 30 秒）
- 每次連上後，機器人服務先送 `hello`。微服務收到後**必須送一次 `config.sync`（全量設定）**
- 心跳使用 WebSocket 協定層的 ping／pong，沒有應用層心跳

## 封包格式

雙向都是 UTF-8 JSON 文字訊息，欄位一律 snake_case：

```json
{
  "type": "message.send",
  "id": "0f8fad5bd9cb469fa16570867728950e",
  "ts": "2026-11-12T02:00:00Z",
  "payload": { }
}
```

| 欄位 | 說明 |
|---|---|
| type | 訊息類型，見下表 |
| id | 訊息 ID，送出方自行產生（建議 UUID），用於 log 追查 |
| ts | 送出時間，UTC RFC3339 |
| payload | 內容物件，依 type 而定 |

## 訊息類型總覽

| 方向 | type | 用途 |
|---|---|---|
| 機器人服務 → 微服務 | `hello` | 連線後第一則，自我介紹 |
| 微服務 → 機器人服務 | `config.sync` | 全量機器人設定；清單外的機器人會被停止 |
| 微服務 → 機器人服務 | `config.upsert` | 新增或更新單一機器人 |
| 微服務 → 機器人服務 | `config.remove` | 移除單一機器人 |
| 機器人服務 → 微服務 | `bot.status` | 機器人狀態變化 |
| 機器人服務 → 微服務 | `channel.snapshot` | 機器人所在公頻的完整線上名單（需開啟 scan_channel_players） |
| 機器人服務 → 微服務 | `channel.members_changed` | 公頻成員進出（需開啟 scan_channel_players） |
| 機器人服務 → 微服務 | `message.received` | 玩家私訊機器人 |
| 微服務 → 機器人服務 | `message.send` | 要求機器人私訊玩家（回覆或主動私訊） |
| 機器人服務 → 微服務 | `message.send_result` | `message.send` 的執行結果 |

未知的 type 會被忽略，方便日後擴充。

---

## hello

```json
{
  "type": "hello",
  "payload": {
    "service_instance_id": "wdbot-01",
    "version": "1.0.0.0",
    "capabilities": ["config.sync", "config.upsert", "config.remove", "message.send"]
  }
}
```

微服務收到後回 `config.sync`。

## config.sync／config.upsert／config.remove

### 機器人設定物件（BotConfig）

| 欄位 | 型別 | 必填 | 說明 |
|---|---|---|---|
| bot_id | string | ✔ | 機器人在後台的唯一 ID，之後所有訊息都用它識別 |
| enabled | bool | | 預設 true；false 時停止此機器人 |
| env | string | | `prod` 正式站或 `test` 測試站，預設 `test` |
| account | string | ✔ | 遊戲帳號 |
| password | string | ✔ | 遊戲密碼 |
| device_id | string | ✔ | 每個機器人**固定且唯一**；建議使用 UUID 格式，與一般手機 App 相同（Android 小寫、iOS 大寫）；token 綁定 device_id，任意更換會導致重新登入，並可能觸發遊戲端的註冊次數限制 |
| device_model | int | | 1 Android、2 iOS、3 APK、4 官網，預設 1 |
| os | string | ✔ | 例如 `Android14` |
| app_version | string | ✔ | 遊戲 App 版本，例如 `1.2.0` |
| phone_model | string | | 選填。手機型號，給 Header 設定中的 `{phone_model}` 變數使用；沒填時用機器人服務的預設值 |
| headers | object | | 選填。這個機器人自己的 HTTP Header，例如 `{"User-Agent": "..."}`；同名時覆寫機器人服務的預設 Header，App API 與聊天 WS 都套用；值為空字串表示不送 |
| auto_mark_read | bool | | 收到私訊後是否自動送已讀，預設 true |
| scan_channel_players | bool | | 上線後是否掃描公頻線上名單並回報成員進出，預設 false |

- 帳號需要**已綁定手機、等級 ≥ 1** 才能發言。等級 0 的帳號仍會登入、進公頻、收私訊、回報名單，`bot.status` 為 online 並在 detail 註明無法發言；送私訊時會收到失敗結果（5008）
- 同一個 env 下，同一個 account 只能給一個 bot_id。遊戲端同帳號連兩條聊天連線會互相踢線，所以第二個會被拒絕並回報 `error`
- 以下欄位變更時，機器人會重新登入：env、account、password、device_id、device_model、os、app_version、phone_model、headers。其他欄位會直接套用
- 機器人因錯誤停止後，對同一個 bot_id 再送一次 `config.upsert`，就會重新啟動

### config.sync

```json
{
  "type": "config.sync",
  "payload": {
    "bots": [
      {
        "bot_id": "bot-001",
        "enabled": true,
        "env": "test",
        "account": "bot0001",
        "password": "Bot0001pass",
        "device_id": "3f2b8c1e-7a4d-4e9b-9c1a-5d6e7f8a9b0c",
        "device_model": 1,
        "os": "Android14",
        "app_version": "1.2.0",
        "auto_mark_read": true,
        "scan_channel_players": true
      }
    ]
  }
}
```

### config.upsert

payload 直接是一個 BotConfig 物件：

```json
{ "type": "config.upsert", "payload": { "bot_id": "bot-001", "enabled": false, "...": "..." } }
```

### config.remove

```json
{ "type": "config.remove", "payload": { "bot_id": "bot-001" } }
```

## bot.status

機器人狀態變化時送出。同一個機器人只保證送出**最新**的狀態，中間狀態可能被合併。每次重連微服務後，機器人服務會重送所有機器人的最新狀態。

```json
{
  "type": "bot.status",
  "payload": {
    "bot_id": "bot-001",
    "state": "online",
    "player_id": 900001,
    "player_name": "Bot0001",
    "level": 1,
    "channel_id": 3,
    "err_code": null,
    "detail": null,
    "updated_at": "2026-11-12T02:00:00Z"
  }
}
```

| state | 說明 |
|---|---|
| starting | 已啟動 |
| logging_in | 登入遊戲中 |
| connecting | 連線聊天中 |
| online | 已上線，可收發私訊 |
| reconnecting | 暫時斷線，會自動重試；detail 說明原因。撞廳後重新分配公頻時也是這個狀態 |
| waiting_channel | 連續多次都被分到已有機器人的公頻，暫時下線，定時再試（見「一廳一機器人」）。這段期間無法收發私訊 |
| stopped | 已停止（停用、移除或服務關閉） |
| error | 因無法自動恢復的原因停止，需要後台處理；detail 與 err_code 說明原因 |

channel_id 是機器人目前佔用的公頻，沒有佔用時不帶。

常見的 `error` 原因：

| 原因 | err_code |
|---|---|
| 帳號密碼錯誤或帳號不存在 | 2041、2002、2003 |
| 參數驗證失敗（例如帳號不是 5–20 字英數、密碼不符合規則） | 2001 |
| 密碼錯誤太多次 | 2023 |
| 帳號預約刪除中 | 2024 |
| 帳號無法使用 | 2045 |
| 帳號停權 | 4038 |
| 聊天權限被關閉 | 無 |
| 聊天連線被踢：變更密碼 | 7002 |
| 聊天連線被踢：預約刪除 | 7003 |
| 聊天連線被踢：停權 | 7004 |
| 聊天連線被踢：帳號已停用 | 7006 |
| 設定欄位缺漏 | 無 |
| 帳號重複 | 無 |

## 一廳一機器人

遊戲伺服器會在機器人連上聊天時，隨機分配一個公頻（大廳），無法自選，也沒有離開公頻的指令。機器人服務預設會確保**一個公頻只有一個機器人**：

1. 機器人連上聊天後，從伺服器的「已加入公頻」推送取得 channel_id
2. 這個公頻沒有其他機器人時，就由它佔用，`bot.status` 變成 online 並帶 channel_id
3. 這個公頻已有其他機器人時，即為撞廳：等約 5–7.5 秒後斷線重連，讓伺服器重新分配
4. 連續撞廳 10 次後，機器人改為 `waiting_channel`，下線 5 分鐘後再試
5. 佔用公頻的機器人斷線或停止時，會釋放該公頻

注意事項：
- 只有佔到公頻的機器人才會送出 `channel.snapshot` 與 `channel.members_changed`
- 撞廳重連期間，機器人仍可收私訊；`waiting_channel` 期間處於下線狀態，玩家私訊收不到，`message.send` 會回 `-2`
- 判斷撞廳的對照表只存在單一機器人服務實例中。**請只執行一個機器人服務實例**，否則不同實例的機器人可能進同一個公頻
- 參數（是否啟用、重試次數、間隔、下線等待時間）在機器人服務的 `appsettings.json` 的 `Bot` 區段設定
- 這個做法有一個前提：重連後伺服器確實可能分配到不同的公頻。如果伺服器的分配規則是「先填滿同一個公頻」或「同一帳號固定回原公頻」，撞廳的機器人會一直分不到空廳，最後都停在 `waiting_channel`

## channel.snapshot

只有 `scan_channel_players` 為 true，而且佔到公頻的機器人才會送出。機器人翻完所在公頻的所有分頁（每頁 100 人）後，送出完整的線上名單。

```json
{
  "type": "channel.snapshot",
  "payload": {
    "bot_id": "bot-001",
    "bot_player_id": 900001,
    "channel_id": 3,
    "channel_name": "公頻 3",
    "online_count": 120,
    "total_count": 350,
    "players": [
      {
        "player_id": 900002,
        "player_name": "小明",
        "level": 12,
        "avatar_id": 70003,
        "avatar_outer_id": 80002
      }
    ]
  }
}
```

- players 不含機器人自己。同一位玩家只出現一次
- **每次重連聊天都會重送**，因為重連後可能被分配到不同的公頻；佔廳期間**遊戲方再次推送成員列表時也會重送**，超過 10 分鐘沒收到推送時機器人會主動掃描一次並重送（機器人服務的 `Bot:ChannelRescanMinutes`）。收到時，請以新名單**取代**該 bot_id 的舊名單，不要合併
- 在 `scan_channel_players` 從 false 改成 true 時，已上線的機器人也會立刻掃描並送出一次
- 掃描失敗時不送 snapshot，改送 `bot.status`（state 仍為 online，detail 說明原因），之後照常回報成員進出
- avatar_url 只在玩家有自訂頭像時才有

## channel.members_changed

遊戲伺服器每 3 秒彙整一次公頻成員進出，機器人收到後轉送。

```json
{
  "type": "channel.members_changed",
  "payload": {
    "bot_id": "bot-001",
    "bot_player_id": 900001,
    "channel_id": 3,
    "online_count": 121,
    "total_count": 351,
    "joined": [
      { "player_id": 900050, "player_name": "阿華", "level": 3, "avatar_id": 70001, "avatar_outer_id": 80001 }
    ],
    "left": [900045]
  }
}
```

- joined 的欄位同 snapshot 的 players；left 是離開玩家的 player_id
- 保證順序：同一次連線內，一定先送 `channel.snapshot`，之後才送 `channel.members_changed`。掃描期間收到的異動會先暫存，snapshot 送出後才依序補送
- 掃描期間有玩家進出時，同一位玩家可能同時出現在 snapshot 與第一批異動中，請以 player_id 去重
- joined 與 left 都是空的時候不會送出

## 主動私訊線上玩家的建議流程

機器人只負責回報名單。要私訊誰、內容是什麼、是否已經私訊過，都由微服務判斷：

1. 收到 `channel.snapshot` 時，更新該機器人的線上名單；收到 `channel.members_changed` 時，增減名單
2. 比對微服務自己的發送紀錄，挑出還沒私訊過、而且符合條件的玩家
3. 依玩家決定內容，送出 `message.send`（`to_player_id` 設為該玩家，`reply_to_message_id` 不帶）
4. 收到 `message.send_result` 且 success 為 true 時，記錄為已私訊

節流注意事項：
- 每個機器人每 2 秒只能私訊 1 則。100 位玩家約需 3.5 分鐘
- 每個機器人的送出佇列上限是 1,000 則，佇列滿時回 `-4`。建議微服務分批送：等收到前一批的 `message.send_result` 再送下一批，不要一次把整份名單丟進來
- 同一位玩家可能被多個機器人回報（例如機器人重連後換了公頻），去重請以 player_id 為準，不要以 bot_id 加 player_id 為準

## message.received

玩家私訊機器人時送出。機器人自己送出的訊息不會回報；同一個 message_id 只回報一次。

```json
{
  "type": "message.received",
  "payload": {
    "bot_id": "bot-001",
    "bot_player_id": 900001,
    "chat_id": "6730a1f2c3d4e5f601234570",
    "message_id": "6730a1f2c3d4e5f601234571",
    "from_player_id": 900002,
    "from_player_name": "小明",
    "text": "你好",
    "message_type": 1,
    "created_at": "2026-11-10T02:01:00Z",
    "reply_message": {
      "message_id": "6730a1f2c3d4e5f601234569",
      "player_id": 900001,
      "player_name": "Bot0001",
      "text": "嗨",
      "message_type": 1,
      "is_not_found": false
    }
  }
}
```

- message_type：1 文字、2 圖片、3 貼圖、4 贈禮、5 大聲公、6 大獎時刻、7 系統訊息。非文字訊息的 text 可能為空
- reply_message：玩家回覆某則訊息時才有；原訊息已不存在時只有 `is_not_found: true`
- 要回覆時，用 `message.send`，把 `to_player_id` 設成 `from_player_id`。如果要以「回覆」形式顯示，再把 `reply_to_message_id` 設成 `message_id`

## message.send

要求機器人私訊玩家。回覆玩家和主動私訊都用這個指令。

```json
{
  "type": "message.send",
  "payload": {
    "request_id": "req-20261112-0001",
    "bot_id": "bot-001",
    "to_player_id": 900002,
    "text": "你好，有什麼可以幫你？",
    "reply_to_message_id": "6730a1f2c3d4e5f601234571"
  }
}
```

| 欄位 | 必填 | 說明 |
|---|---|---|
| request_id | ✔ | 微服務產生的唯一 ID，`message.send_result` 會原樣帶回 |
| bot_id | ✔ | 由哪個機器人送出 |
| to_player_id | ✔ | 接收玩家 ID |
| text | ✔ | 文字內容，會去頭尾空白；超過 200 字自動拆成多則依序送出 |
| reply_to_message_id | | 要回覆的訊息 ID，只套用在第一則 |

送出規則：
- 同一個機器人的指令依序排隊送出，遊戲限制每 2 秒 1 則；10 則長訊息約需 20 秒以上
- 不需要先加好友
- 玩家封鎖機器人時，遊戲端仍回成功，但玩家收不到

## message.send_result

每個 `message.send` 都會有一則結果。

```json
{
  "type": "message.send_result",
  "payload": {
    "request_id": "req-20261112-0001",
    "bot_id": "bot-001",
    "success": true,
    "message_ids": ["6730a1f2c3d4e5f601234580"],
    "err_code": 0,
    "err_msg": null
  }
}
```

- message_ids：成功送出的訊息 ID，依序排列。拆成多則時，失敗結果也會列出失敗前已送出的部分
- err_code 為正數時，是遊戲聊天 WS 的錯誤碼：

| err_code | 說明 |
|---|---|
| 5008 | 未達發話等級 |
| 5011 | 聊天功能已關閉 |
| 11007 | 訊息超過字數 |
| 11012 | 發送私訊失敗，例如私訊自己 |
| 11026 | 訊息為空 |

- err_code 為負數時，是機器人服務的本地錯誤：

| err_code | 說明 | 建議處理 |
|---|---|---|
| -1 | 參數錯誤，例如 to_player_id ≤ 0 | 修正後再送 |
| -2 | 聊天未連線（等待 30 秒仍未恢復，包含 `waiting_channel` 期間） | 稍後重送，或改由其他在線的機器人送出 |
| -3 | 等待遊戲回應逾時，**無法確定是否已送達** | 視情況決定是否重送 |
| -4 | 機器人送出佇列已滿 | 降低送出頻率 |
| -5 | 機器人不存在、未啟用或尚未登入完成 | 檢查 bot.status |
| -6 | 等待回應時斷線，**無法確定是否已送達** | 視情況決定是否重送 |

## 可靠性說明

- 微服務斷線期間，機器人服務會暫存 `message.received`、`message.send_result`、`channel.snapshot` 與 `channel.members_changed`，合計最多 10,000 則，超過時丟棄最舊的；重連後補送
- 狀態（`bot.status`）不排隊，只保留最新一筆，重連後重送
- 微服務斷線期間，機器人仍維持遊戲登入與聊天連線
- 機器人服務重啟時，未送出的 `message.send` 會遺失。重啟後沒有收到 `message.send_result` 的 request，微服務需要自行決定是否重送
- 微服務應該用 `message_id` 對 `message.received` 去重：機器人服務只在單一程序內去重，服務重啟後不保證

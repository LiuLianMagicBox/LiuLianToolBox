# LiuLianToolBox 專案指引

LiuLianToolBox 的目標是讓兩部已配對的 PC 同步 Discord 語音頻道，也讓使用者從一部 PC 呼叫另一部 PC 執行動作。每部 PC 各自執行一個 ToolBox 用戶端。「本機」指目前操作的 PC，「遠端」指與它配對的另一部 PC。

## 技術及語言

- 桌面端採用 **C# / .NET**。設定介面可用 .NET 資源檔管理譯文，鬧鐘可用計時器觸發，啟動本機應用程式則可用 `System.Diagnostics.Process`。
- 支援 `ZH_TW`（繁體中文，預設）、`ZH_CN`（簡體中文）及 `EN`（英文），分別對應 .NET 語言標籤 `zh-TW`、`zh-CN` 及 `en`。選單、通知及錯誤訊息均使用語言資源，三個譯本沿用相同的資源鍵。首次啟動或缺少個別譯文時使用 `ZH_TW`。[.NET 語言資源](https://learn.microsoft.com/en-us/dotnet/core/extensions/localization)

## 設定選單

選單順序如下。標明「只限獲授權使用者」的項目只在取得相應權限後出現。LiuLianBot 收到這兩項功能的設定或執行請求時，亦須核對相應權限。

| EN | ZH_TW（預設） | ZH_CN | 用途 |
| --- | --- | --- | --- |
| Home | 首頁 | 首页 | 顯示兩部 PC 的連線狀態、所在語音頻道及最近一次動作的結果。 |
| Current PC Config | 本機設定 | 本机设置 | 設定本機名稱、介面語言、可供呼叫的應用程式及畫面分享來源。 |
| Remote PC Config | 遠端電腦設定 | 远程电脑设置 | 選擇配對的 PC，設定語音頻道跟隨、同頻道畫面分享開關及負責分享的 PC。 |
| Discord Bot Config | Discord 機器人設定 | Discord 机器人设置 | 連結 LiuLianBot，選擇預設鬧鐘用戶端，管理自己的私人語音頻道。 |
| IDLE/Auto-Reply Config | 閒置與自動回覆設定 | 空闲与自动回复设置 | 只限獲授權使用者。設定閒置條件、自動回覆開關及回覆內容。 |
| Message Box Config | 留言箱設定 | 留言箱设置 | 只限獲授權使用者。啟用留言箱，選擇自己的電郵或 Discord 私訊作為收件方式。 |
| Plugins | 外掛程式 | 插件 | 顯示 TODO/WIP。構想是讓外掛提供類似 Stream Deck 的可設定動作，載入及觸發方式待驗證。 |
| Software Info | 軟體資訊 | 软件信息 | 顯示 ToolBox 版本。 |

## 雙 PC 動作

1. **加入同一個 Discord 語音頻道。** 例如 PC A 加入某個頻道後，已啟用跟隨的 PC B 應讓其 Discord 帳戶加入同一頻道。以伺服器及頻道 ID 比對，兩個帳戶已在同一頻道時便完成同步，避免互相重複觸發。使用 Discord 官方支援且已獲授權的方式。採用桌面 RPC 時須核實應用程式的存取資格，將已在其他頻道的使用者移走前須取得其同意。若無法自動加入，顯示目標頻道供使用者加入。[Discord RPC](https://docs.discord.com/developers/topics/rpc#select_voice_channel)
2. **同頻道時開啟畫面分享。** 在設定中選擇是否啟用、由哪部 PC 分享，以及該機的螢幕或應用程式視窗。確認兩個 Discord 帳戶已進入同一個語音頻道後，才在所選 PC 觸發分享。自動開始分享的介面仍待驗證。現階段的設計是在該機提示使用者於 Discord 選取指定來源並按「Go Live」。原定視窗已關閉時，提示重新選擇。[Discord 畫面分享說明](https://support.discord.com/hc/en-us/articles/360040816151-Go-Live-and-Screen-Share)
3. **在另一部 PC 開啟應用程式。** 每部 PC 在本機設定可供啟動的程式。發出請求時選擇目標 PC 和程式，接收端核對配對身分，按自己的清單解析程式並啟動，再回報結果。例如從 A 呼叫 B 的音樂播放器，程式應在 B 開啟。B 離線或找不到程式時，讓 A 顯示原因。

## LiuLianBot 整合規劃

LiuLianBot 是獨立的 Go 專案。它已有 slash command 註冊、權限判斷及私人語音頻道擁有權紀錄。擴充時可從該專案的 `discord-part/tools/interaction_args.json` 及 `discord-part/internal/bot/voice.go` 入手。Bot 憑證由 LiuLianBot 保管。

- **鬧鐘。** 使用者透過 LiuLianBot 的 slash command 設定時間，可指定自己的 ToolBox 用戶端。省略目標時，採用該使用者在 `Discord Bot Config` 選定的預設用戶端。回覆須列明目標 PC 和響鬧時間。例如預設為 A 而指令指定 B，鬧鐘就只應在 B 響起。未設預設值時請使用者選擇，目標離線時回報狀態。
- **私人語音頻道。** 在 `Discord Bot Config` 顯示使用者擁有的頻道，並可修改頻道名稱和人數上限。儲存時由 LiuLianBot 再核對擁有權及 Discord 權限，避免擁有權轉移後舊擁有人仍可修改。
- **閒置自動回覆。** 獲授權使用者可設定聯絡人在其閒置期間會收到的回覆。有人透過 LiuLianBot 聯絡該使用者時，Bot 會檢查開關及閒置狀態，以 Bot 身分發送設定好的回覆。使用者恢復活動後，停止自動回覆。
- **留言箱。** 如獲授權使用者已啟用留言箱，在閒置回覆中提供留言入口。聯絡人透過 Bot 留言，訊息先交給 MailServer 中轉，再按收件人的設定寄到其本人電郵，或由 LiuLianBot 轉發到其 Discord 私訊（DM）。實作時須與 MailServer 確認交接方式及送達回報。

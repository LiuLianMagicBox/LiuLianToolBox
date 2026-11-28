# LiuLianBot 的 ToolBox 整合

官方 LiuLianBot 的標準執行檔已內建桌面 API。Bot 登入 Discord 時，會一併註冊 `/toolbox link`、`unlink`、`alarm`、`contact`、`message` 及 `receipt`。在 Discord 的 `/help` 選擇 `command: toolbox` 可查看各項用法。[官方 Bot](https://github.com/LiuLianMagicBox/LiuLianBot) · [指令參考](https://github.com/LiuLianMagicBox/LiuLianBot/blob/master/discord-part/tools/interaction_args.md)

## 建置與更新

在包含 `9e21fc1` 的官方 LiuLianBot checkout 執行以下指令，檢查新執行檔包含的子指令。按原有部署方式更新及重啟正在運行的 Bot，讓它登入 Discord 及同步指令。[加入指令的提交](https://github.com/LiuLianMagicBox/LiuLianBot/commit/9e21fc13724c245ff05080d67d2a1f23dd9488b5) · [正式服務更新](https://github.com/LiuLianMagicBox/LiuLianBot/blob/master/deploy/systemd/README.md#upgrade-or-roll-back)

```sh
bash start.sh build
./build/liulianbot commands
```

在 LiuLianToolBox 儲存庫亦可執行 `python3 integrations/liulianbot/build.py ../LiuLianBot`，將 Bot checkout 路徑換成實際位置。建置工具匯出該 checkout 的 Git `HEAD`，輸出 `artifacts/liulianbot-toolbox`。遇到舊版 Bot（例如 `d565e488`）時會套用相容擴充，該版本需要明確設定 `LIULIAN_TOOLBOX_CONFIG` 才會註冊指令。

## 主機設定

1. 標準 Bot 預設在 `127.0.0.1:48750` 提供桌面 API，裝置資料保存在 Bot 的 `discord-part/toolbox-state.json`。連結裝置、配對及鬧鐘可用預設設定。服務帳戶需要能在狀態檔所在目錄寫入。
2. 如需自訂設定，在 Bot 主機建立 `discord-part/toolbox.json`，或以 `LIULIAN_TOOLBOX_CONFIG` 指向私人設定檔。相對的 `stateFile` 路徑會以設定檔所在目錄為準。首次加入設定檔或更改監聽埠後請重啟 Bot。[完整設定範例](config.example.json)
3. 在 `users` 以 Discord 使用者 ID 分別授予 `idle` 和 `mailbox`。需要接收電郵時，在管理員已確認地址屬於該收件人後填寫 `email`。桌面端只能選擇收件方式。Bot 在設定、聯絡及留言時重新讀取權限，撤銷權限會在下一次同步隱藏對應選單。

將 HTTPS 反向代理指向設定中的回環位址，例如 `127.0.0.1:48750`。將服務的 HTTPS 根網址交給桌面使用者，例如 `https://toolbox.example.org`。代理亦可使用路徑前綴，轉送時移除該前綴，使 Bot 收到 `/v1/…`。請在代理保留 `Authorization` 標頭，並讓請求可等待至少 25 秒。用戶端約每兩秒同步，服務在十五秒未收到同步後會將該電腦視為離線。

## MailServer

Bot 使用驗證憑證的 SMTPS 向 MailServer 提交郵件，預設使用 465 埠。設定 `smtp.host`、寄件地址 `from`、登入帳戶 `username` 及接收 DM 中轉的專用信箱 `relayInbox`。SMTP 密碼由 Bot 的環境變數 `LIULIAN_TOOLBOX_SMTP_PASSWORD` 提供。寄件地址須獲 MailServer 准許使用。

- **電郵**寄往 `users` 內該 Discord 帳戶的已驗證地址。SMTP 接受後回條顯示已排入佇列。如果伺服器提供 DSN，Bot 會要求成功、延遲及失敗通知。請讓寄往信封寄件人 `from` 的 DSN 進入下方的專用信箱。收到相符的投遞通知後會更新回條。未提供終端通知的郵件只顯示最後已知狀態。[SMTP DSN](https://datatracker.ietf.org/doc/html/rfc3461) [投遞報告格式](https://datatracker.ietf.org/doc/html/rfc3464)
- **Discord 私訊**先寄往 `relayInbox`。MailServer 收到郵件後，轉送程式才會將原文及該封郵件的專用憑據交回 Bot。Bot 核對原文、對應收件人及當前留言箱權限，再發送 DM。已確認的重複回條會回覆成功。Discord 結果不明時會保留待核實狀態以供追查。

在可存取專用信箱的主機提供 `TOOLBOX_IMAP_HOST`、`TOOLBOX_IMAP_USER`、`TOOLBOX_IMAP_PASSWORD` 及 `TOOLBOX_URL`，然後定期執行轉送程式。它透過 IMAPS 993 讀取未讀郵件，Bot 確認後才標為已讀。可使用相同的專用信箱接收 DM 中轉與 DSN。SMTP 密碼、IMAP 密碼及真實收件地址均應放在部署環境。

```sh
python3 integrations/mailserver/forward_mail.py
```

## 整合測試

在 Linux 或 macOS 安裝 Go 的 race detector 所需 C 編譯器後，可在根目錄執行以下命令。

```sh
python3 integrations/liulianbot/build.py ../LiuLianBot --test --dotnet dotnet
```

測試使用本機 Discord 回應、臨時 SMTP 服務及真正的 Go HTTP 處理流程。提供 `LIULIANBOT_TEST_MYSQL` 即可一併執行私人頻道擁有權轉移與權限撤銷測試。該變數使用 Go MySQL DSN 格式，須指向容許建立和刪除臨時 schema 的專用測試實例。未設定時只有這項資料庫測試會略過。

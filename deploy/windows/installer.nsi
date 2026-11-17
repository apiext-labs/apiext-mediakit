; ─────────────────────────────────────────────────────────────────────────────
; Apiext MediaKit Windows 安装包（NSIS 3.x / MUI2）
;
; 打包内容（与本文件同目录）：
;   app\       publish-app.ps1 的输出（自包含发布，含 Apiext.MediaKit.Server.exe）
;   zlm\       ZLMediaKit Windows 版（MediaServer.exe、default.pem、www\record）
;   ffmpeg\    ffmpeg.exe
;   nssm.exe   服务托管器
;   install.ps1 / uninstall.ps1 / detect-ip.ps1
;
; 编译： makensis installer.nsi   或   powershell -File build-installer.ps1
; ─────────────────────────────────────────────────────────────────────────────

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64
RequestExecutionLevel admin

!define APP_NAME        "Apiext MediaKit 流媒体管理平台"
!define APP_SHORT       "Apiext-MediaKit"
!define APP_VERSION     "1.0.0"
!define APP_PUBLISHER   "Apiext"
!define APP_EXE         "Apiext.MediaKit.Server.exe"
!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\Apiext-MediaKit"

Name "${APP_NAME}"
OutFile "Apiext-MediaKit-Setup-${APP_VERSION}.exe"
InstallDir "C:\apiext-mediakit"
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
BrandingText "${APP_NAME} ${APP_VERSION}"

VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey "FileVersion"     "${APP_VERSION}"
VIAddVersionKey "ProductVersion"  "${APP_VERSION}"
VIAddVersionKey "LegalCopyright"  "© ${APP_PUBLISHER}"

; ───────────────────────────── 界面 ─────────────────────────────

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "TextFunc.nsh"
!insertmacro TrimNewLines

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!define MUI_WELCOMEPAGE_TITLE "安装 ${APP_NAME}"
!define MUI_WELCOMEPAGE_TEXT "本向导将安装：$\r$\n$\r$\n  · ${APP_NAME}（应用服务）$\r$\n  · ZLMediaKit 流媒体引擎（流媒体服务）$\r$\n  · ffmpeg（兼容取流 / 抓拍）$\r$\n$\r$\n安装后两个服务将开机自启；管理平台默认端口 28080（与 Docker 版一致）。$\r$\n$\r$\n点击「下一步」继续。"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY

; 自定义参数页 1：对外 IP + 应用端口
Page custom ConfigPageCreate ConfigPageLeave
; 自定义参数页 2：流媒体端口
Page custom PortPageCreate PortPageLeave

!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_TITLE "安装完成"
; 注意：MUI 完成页文本里不要写运行时变量（$Var 会被 MUI 内部拼接吞掉），
; 地址与凭据统一走「安装报告」和「管理平台.url」快捷方式。
!define MUI_FINISHPAGE_TEXT "管理平台已启动。$\r$\n$\r$\n访问地址、账号密码与各项密钥见「安装报告」（安装目录下 install-report.txt）。$\r$\n$\r$\n如需对外访问，请确认防火墙规则 Apiext-MediaKit-TCP / Apiext-MediaKit-UDP 已生效。"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\install-report.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "查看安装报告（账号密码 / 密钥 / 端口）"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!define MUI_FINISHPAGE_RUN "$INSTDIR\管理平台.url"
!define MUI_FINISHPAGE_RUN_TEXT "打开管理平台页面"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ───────────────────────── 自定义参数页 ─────────────────────────

Var PublicHost
Var AppPort
Var ZlmHttpPort
Var ZlmRtspPort
Var ZlmRtmpPort
Var ZlmRtcPort
Var ZlmSrtPort
Var ZlmRtpPort
Var GbSipPort
Var RtpPortRange
Var AppUrl
Var HPublicHost
Var HAppPort
Var HZlmHttp
Var HZlmRtsp
Var HZlmRtmp
Var HZlmRtc
Var HZlmSrt
Var HZlmRtp
Var HGbSip
Var HRtpRange

; NSIS 会把 "$Var:" 连读成变量名，这里分段拼出 http://IP:端口/ 避免解析歧义
Function BuildAppUrl
    StrCpy $AppUrl "http://$PublicHost"
    StrCpy $AppUrl "$AppUrl:"
    StrCpy $AppUrl "$AppUrl$AppPort/"
FunctionEnd

; ── 端口校验用的小工具 ──────────────────────────────────────────
; 入参：$R0=端口值  $R1=显示名（用于报错）
; 出参：$R9=1 表示有错（已弹框），调用方据此 Abort
Function ValidatePort
    ${If} $R0 == ""
        MessageBox MB_ICONEXCLAMATION "「$R1」端口不能为空。"
        StrCpy $R9 1
        Return
    ${EndIf}
    ${If} $R0 < 1
    ${OrIf} $R0 > 65535
        MessageBox MB_ICONEXCLAMATION "「$R1」端口 $R0 超出范围，应为 1-65535。"
        StrCpy $R9 1
        Return
    ${EndIf}
FunctionEnd

; 重复检测：把已用过的端口写进临时 ini，能读到就是重复（NSIS 无数组，借 ini 当集合用）
; 入参：$R0=端口值  $R1=显示名  出参：$R9=1 表示重复
Function CheckPortDup
    ReadINIStr $R3 "$PLUGINSDIR\apiext-mediakit-ports.ini" "p" "$R0"
    ${If} $R3 != ""
        MessageBox MB_ICONEXCLAMATION "端口 $R0 重复填写（$R1），请为每项指定不同端口。"
        StrCpy $R9 1
    ${Else}
        WriteINIStr "$PLUGINSDIR\apiext-mediakit-ports.ini" "p" "$R0" "1"
    ${EndIf}
FunctionEnd

; 读取一个端口输入框并完成 空值/范围/重复 三项校验
!macro CheckPortField CTRL VAR NAME
    ${NSD_GetText} ${CTRL} ${VAR}
    StrCpy $R0 ${VAR}
    StrCpy $R1 "${NAME}"
    Call ValidatePort
    ${If} $R9 == 0
        Call CheckPortDup
    ${EndIf}
!macroend

Function .onInit
    StrCpy $AppPort "28080"
    StrCpy $ZlmHttpPort "28180"
    StrCpy $ZlmRtspPort "28554"
    StrCpy $ZlmRtmpPort "28335"
    StrCpy $ZlmRtcPort "28000"
    StrCpy $ZlmSrtPort "29000"
    StrCpy $ZlmRtpPort "21000"
    StrCpy $GbSipPort "28060"
    StrCpy $RtpPortRange "24000-24500"

    ; 预填本机 IPv4（best-effort，取不到就让用户手填）
    InitPluginsDir
    File /oname=$PLUGINSDIR\detect-ip.ps1 "detect-ip.ps1"
    nsExec::ExecToStack 'powershell -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\detect-ip.ps1"'
    Pop $0
    Pop $1
    ${If} $0 == 0
        ${TrimNewLines} $1 $1
        StrCpy $PublicHost $1
    ${EndIf}

    Call BuildAppUrl
FunctionEnd

; ─────────────────── 第 1 页：访问地址 + 应用端口 ───────────────────

Function ConfigPageCreate
    !insertmacro MUI_HEADER_TEXT "配置访问地址" "填写浏览器与国标设备可访问的本机地址"

    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
        Abort
    ${EndIf}

    ${NSD_CreateLabel} 0 0 100% 22u "对外 IP 用于浏览器访问管理平台，也是国标设备的推流目标地址。$\r$\n多网卡（Hyper-V / VMware 虚拟网卡）请填写真实的局域网地址。"
    Pop $0

    ${NSD_CreateLabel} 0 30u 78u 12u "对外 IP / 主机名："
    Pop $0
    ${NSD_CreateText} 80u 28u 180u 13u "$PublicHost"
    Pop $HPublicHost

    ${NSD_CreateLabel} 0 52u 78u 12u "应用端口 (Web)："
    Pop $0
    ${NSD_CreateNumber} 80u 50u 52u 13u "$AppPort"
    Pop $HAppPort
    ${NSD_CreateLabel} 136u 52u 150u 12u "默认 28080，改后访问地址同步变"
    Pop $0

    ${NSD_CreateLabel} 0 78u 100% 24u "流媒体端口（HTTP / RTSP / RTP / 国标 SIP 等）在下一页配置，$\r$\n默认值一般无需修改；安装时会自动放行这些端口。"
    Pop $0

    nsDialogs::Show
FunctionEnd

Function ConfigPageLeave
    ${NSD_GetText} $HPublicHost $PublicHost
    ${NSD_GetText} $HAppPort $AppPort

    ${If} $PublicHost == ""
        MessageBox MB_ICONEXCLAMATION "请填写对外 IP / 主机名。"
        Abort
    ${EndIf}
    StrCpy $2 $PublicHost 1
    ${If} $2 == " "
        MessageBox MB_ICONEXCLAMATION "对外 IP 不能以空格开头。"
        Abort
    ${EndIf}

    StrCpy $R0 $AppPort
    StrCpy $R1 "应用 (Web)"
    StrCpy $R9 0
    Call ValidatePort
    ${If} $R9 != 0
        Abort
    ${EndIf}

    Call BuildAppUrl
FunctionEnd

; ─────────────────── 第 2 页：流媒体端口 ───────────────────

Function PortPageCreate
    !insertmacro MUI_HEADER_TEXT "配置流媒体端口" "端口被占用时才需要修改，默认值一般无需改动"

    nsDialogs::Create 1018
    Pop $0
    ${If} $0 == error
        Abort
    ${EndIf}

    ${NSD_CreateLabel} 0 0 100% 22u "下面这些端口用于取流、播放与国标收流，只有在本机被占用时才需要改。$\r$\n修改后需与上级平台 / 对接设备同步（国标 SIP 与 RTP 收流尤其要对上）。"
    Pop $0

    ; 第 1 行：HTTP / RTSP / RTMP
    ${NSD_CreateLabel} 0 28u 42u 12u "HTTP"
    Pop $0
    ${NSD_CreateNumber} 44u 26u 46u 13u "$ZlmHttpPort"
    Pop $HZlmHttp

    ${NSD_CreateLabel} 96u 28u 42u 12u "RTSP"
    Pop $0
    ${NSD_CreateNumber} 140u 26u 46u 13u "$ZlmRtspPort"
    Pop $HZlmRtsp

    ${NSD_CreateLabel} 192u 28u 46u 12u "RTMP"
    Pop $0
    ${NSD_CreateNumber} 236u 26u 46u 13u "$ZlmRtmpPort"
    Pop $HZlmRtmp

    ; 第 2 行：WebRTC / SRT / 国标 SIP
    ${NSD_CreateLabel} 0 46u 42u 12u "WebRTC"
    Pop $0
    ${NSD_CreateNumber} 44u 44u 46u 13u "$ZlmRtcPort"
    Pop $HZlmRtc

    ${NSD_CreateLabel} 96u 46u 42u 12u "SRT"
    Pop $0
    ${NSD_CreateNumber} 140u 44u 46u 13u "$ZlmSrtPort"
    Pop $HZlmSrt

    ${NSD_CreateLabel} 192u 46u 46u 12u "国标 SIP"
    Pop $0
    ${NSD_CreateNumber} 236u 44u 46u 13u "$GbSipPort"
    Pop $HGbSip

    ; 第 3 行：国标 RTP 收流 + UDP 端口范围
    ${NSD_CreateLabel} 0 64u 42u 12u "RTP 收流"
    Pop $0
    ${NSD_CreateNumber} 44u 62u 46u 13u "$ZlmRtpPort"
    Pop $HZlmRtp

    ${NSD_CreateLabel} 96u 64u 42u 12u "RTP UDP"
    Pop $0
    ${NSD_CreateText} 140u 62u 90u 13u "$RtpPortRange"
    Pop $HRtpRange
    ${NSD_CreateLabel} 234u 64u 48u 12u "起始-结束"
    Pop $0

    ${NSD_CreateLabel} 0 82u 100% 24u "· UDP 范围填「起始-结束」，国标点播黑屏优先查这里是否被防火墙拦住。$\r$\n· 安装脚本会把这些端口加入防火墙入站规则 Apiext-MediaKit-TCP / Apiext-MediaKit-UDP。"
    Pop $0

    nsDialogs::Show
FunctionEnd

Function PortPageLeave
    StrCpy $R9 0
    ; 清掉上一次（点「上一步」再回来）留下的集合文件
    Delete "$PLUGINSDIR\apiext-mediakit-ports.ini"

    !insertmacro CheckPortField $HZlmHttp  $ZlmHttpPort  "ZLM HTTP"
    !insertmacro CheckPortField $HZlmRtsp  $ZlmRtspPort  "RTSP"
    !insertmacro CheckPortField $HZlmRtmp  $ZlmRtmpPort  "RTMP"
    !insertmacro CheckPortField $HZlmRtc   $ZlmRtcPort   "WebRTC"
    !insertmacro CheckPortField $HZlmSrt   $ZlmSrtPort   "SRT"
    !insertmacro CheckPortField $HGbSip    $GbSipPort    "国标 SIP"
    !insertmacro CheckPortField $HZlmRtp   $ZlmRtpPort   "RTP 收流"

    ${If} $R9 != 0
        Abort
    ${EndIf}

    ; UDP 端口范围：只做轻量校验（格式严格性由 install.ps1 兜底）
    ${NSD_GetText} $HRtpRange $RtpPortRange
    ${If} $RtpPortRange == ""
        MessageBox MB_ICONEXCLAMATION "UDP 端口范围不能为空，形如 24000-24500。"
        Abort
    ${EndIf}
    ${If} $RtpPortRange == "24000-24500"
        ; 默认值，无需再校验
    ${Else}
        StrCpy $R2 $RtpPortRange 1
        ${If} $R2 == "-"
            MessageBox MB_ICONEXCLAMATION "UDP 端口范围格式不对，应形如 24000-24500。"
            Abort
        ${EndIf}
        StrCpy $R2 $RtpPortRange 1 -1
        ${If} $R2 == "-"
            MessageBox MB_ICONEXCLAMATION "UDP 端口范围格式不对，应形如 24000-24500。"
            Abort
        ${EndIf}
    ${EndIf}

    Delete "$PLUGINSDIR\apiext-mediakit-ports.ini"
FunctionEnd

Function .onInstSuccess
    ; 生成「管理平台.url」供完成页一键打开（在安装完成后写入，确保端口/地址是最新的）
    WriteINIStr "$INSTDIR\管理平台.url" "InternetShortcut" "URL" "$AppUrl"
FunctionEnd

; ───────────────────────────── 安装 ─────────────────────────────

Section "Install"
    ; 载荷先解到临时目录，再由 install.ps1 复制到 $INSTDIR（避免重装时自覆盖）
    SetOutPath "$PLUGINSDIR\payload"
    File /r "app\*.*"
    File /r "zlm\*.*"
    File /r "ffmpeg\*.*"
    File "nssm.exe"
    File "install.ps1"
    File "uninstall.ps1"

    CreateDirectory "$INSTDIR\tools"
    CopyFiles /SILENT "$PLUGINSDIR\payload\install.ps1" "$INSTDIR\tools\"
    CopyFiles /SILENT "$PLUGINSDIR\payload\uninstall.ps1" "$INSTDIR\tools\"

    DetailPrint "正在生成配置、放行端口、注册服务（约 1-2 分钟）..."
    nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\payload\install.ps1" -InstallDir "$INSTDIR" -PublicHost "$PublicHost" -AppPort $AppPort -ZlmHttpPort $ZlmHttpPort -ZlmRtspPort $ZlmRtspPort -ZlmRtmpPort $ZlmRtmpPort -ZlmRtcPort $ZlmRtcPort -ZlmSrtPort $ZlmSrtPort -ZlmRtpPort $ZlmRtpPort -GbSipPort $GbSipPort -RtpPortRange "$RtpPortRange" -Force'
    Pop $0
    ${If} $0 != 0
    ${AndIf} $0 != "ok"
        MessageBox MB_ICONSTOP "安装失败（返回：$0）。$\r$\n$\r$\n常见原因：端口填写非法 / 端口被占用 / 权限不足。$\r$\n点「显示详细信息」可看到脚本输出的具体报错。$\r$\n$\r$\n也可查看：$\r$\n  $INSTDIR\logs\app.err.log$\r$\n  或 事件查看器 → Windows 日志 → 应用程序$\r$\n$\r$\n修复后可重新运行安装包，数据目录 APP_DATA 不会被删除。"
        Abort
    ${EndIf}

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKLM "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION}"
    WriteRegStr HKLM "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
    WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
    WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
SectionEnd

; ───────────────────────────── 卸载 ─────────────────────────────

Section "Uninstall"
    DetailPrint "停止并删除服务..."
    IfFileExists "$INSTDIR\tools\uninstall.ps1" 0 +3
    nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\tools\uninstall.ps1" -InstallDir "$INSTDIR"'
    Pop $0

    DeleteRegKey HKLM "${UNINST_KEY}"

    MessageBox MB_YESNO|MB_ICONEXCLAMATION "是否同时删除安装目录？$\r$\n$\r$\n是：删除全部（含数据库 APP_DATA、录像文件，不可恢复）$\r$\n否：仅删除程序，保留数据与录像" IDNO keep_data

    RMDir /r "$INSTDIR"
    Goto uninstall_done

    keep_data:
        RMDir /r "$INSTDIR\app"
        RMDir /r "$INSTDIR\ffmpeg"
        RMDir /r "$INSTDIR\tools"
        RMDir /r "$INSTDIR\logs"
        Delete "$INSTDIR\zlm\MediaServer.exe"
        Delete "$INSTDIR\zlm\default.pem"
        Delete "$INSTDIR\zlm\config.ini"
        Delete "$INSTDIR\zlm\run-zlm.cmd"
        Delete "$INSTDIR\install-report.txt"
        Delete "$INSTDIR\管理平台.url"
        Delete "$INSTDIR\Uninstall.exe"
        RMDir "$INSTDIR\zlm"      ; www\record 非空时不会删除
        RMDir "$INSTDIR"          ; APP_DATA 非空时不会删除

    uninstall_done:
SectionEnd

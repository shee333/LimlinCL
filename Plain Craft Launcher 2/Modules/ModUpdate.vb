''' <summary>
''' 社区更新模块：可插拔的更新源骨架。
''' <para>
'''     上游开源版的更新功能原本为空壳。这里保留完整的更新入口与 UI 流程，
'''     接入自己的更新源时，只需填写 <see cref="UpdateSourceUrl"/> 即可，
'''     无需改动任何调用方代码。
''' </para>
''' <para>
'''     更新源需要返回如下 JSON（UTF-8）：
'''     <code>
'''     {
'''       "version": "0.2.0",
'''       "versionCode": 20,
'''       "url": "https://你的域名/下载页或直链",
'''       "notes": "本次更新内容……"
'''     }
'''     </code>
'''     <see cref="UpdateInfo.VersionCode"/> 与启动器内置的 VersionCode 比较，更大时才提示更新。
''' </para>
''' <para>
'''     安全约定：本模块只做「检查 + 提示」，永远不会自动下载或替换启动器文件；
'''     自动更新行为需要在此处显式实现，因此不会出现被静默更新成其他版本的情况。
''' </para>
''' </summary>
Public Module ModUpdate

#Region "配置"

    ' ==================== 社区更新源配置（接入时只改这里） ====================

    ''' <summary>
    ''' 社区更新源地址（返回 JSON）。留空表示不检查更新（当前默认）。
    ''' </summary>
    Public Const UpdateSourceUrl As String = ""

    ''' <summary>
    ''' 是否在启动后于后台静默检查更新。<para/>
    ''' 即使开启也只弹出提示，不会自动下载或替换文件。
    ''' </summary>
    Public Const AutoUpdateEnabled As Boolean = False

    ''' <summary>后台检查的延迟（毫秒），避免拖慢启动。</summary>
    Private Const BackgroundCheckDelay As Integer = 5000

#End Region

#Region "入口"

    ''' <summary>
    ''' 用户手动点击「检查更新」时调用。
    ''' </summary>
    Friend Sub CheckByButton()
        If String.IsNullOrWhiteSpace(UpdateSourceUrl) Then
            Hint("社区更新源尚未配置，当前版本不会检查更新。", HintType.Blue)
            Return
        End If
        RunInNewThread(
            Sub()
                Try
                    Dim Info As UpdateInfo = FetchUpdateInfo()
                    If Info Is Nothing Then Return
                    If Info.VersionCode <= VersionCode Then
                        Logger.Info($"社区更新：已是最新版本（当前 {VersionBaseName} / code {VersionCode}，远端 {Info.Version} / code {Info.VersionCode}）")
                        Hint("当前已是最新版本。", HintType.Green)
                        Return
                    End If
                    Logger.Info($"社区更新：发现新版本 {Info.Version}（code {Info.VersionCode}）")
                    Dim Text As String = $"发现新版本 {Info.Version}（当前 {VersionBaseName}）{vbCrLf}{vbCrLf}"
                    If Not String.IsNullOrWhiteSpace(Info.Notes) Then Text &= Info.Notes.Trim() & vbCrLf & vbCrLf
                    If String.IsNullOrWhiteSpace(Info.Url) Then
                        MyMsgBox(Text, "社区更新")
                    ElseIf MyMsgBox(Text & "是否前往下载？", "社区更新", "前往下载", "稍后") = 1 Then
                        OpenWebsite(Info.Url)
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "检查社区更新失败")
                    Hint("检查更新失败，请稍后再试。", HintType.Red)
                End Try
            End Sub, "检查社区更新", ThreadPriority.BelowNormal)
    End Sub

    ''' <summary>
    ''' 启动时调用：未开启自动检查、或未配置更新源时直接返回，不发起任何请求。
    ''' </summary>
    Friend Sub StartBackgroundCheck()
        If Not AutoUpdateEnabled Then Return
        If String.IsNullOrWhiteSpace(UpdateSourceUrl) Then Return
        RunInNewThread(
            Sub()
                Try
                    Thread.Sleep(BackgroundCheckDelay)
                    Dim Info As UpdateInfo = FetchUpdateInfo()
                    If Info Is Nothing Then Return
                    If Info.VersionCode <= VersionCode Then Return
                    Hint($"发现启动器新版本 {Info.Version}，可在「设置 → 关于」中查看详情。", HintType.Blue)
                Catch ex As Exception
                    Logger.Info($"后台检查社区更新失败：{ex.Message}")
                End Try
            End Sub, "后台检查社区更新", ThreadPriority.BelowNormal)
    End Sub

#End Region

#Region "实现"

    ''' <summary>更新源返回的信息。</summary>
    Private Class UpdateInfo
        Public Version As String = ""
        Public VersionCode As Integer = 0
        Public Url As String = ""
        Public Notes As String = ""
    End Class

    ''' <summary>请求并解析更新源信息。</summary>
    Private Function FetchUpdateInfo() As UpdateInfo
        Dim Json As JObject = NetRequestByClientRetry(UpdateSourceUrl, RequireJson:=True).DeserializeJson()
        If Json Is Nothing Then Throw New Exception("更新源返回内容为空")
        Dim Info As New UpdateInfo
        If Json("version") Is Nothing Then Throw New Exception("更新源缺少 version 字段")
        Info.Version = Json("version").ToString.Trim
        If Json("versionCode") IsNot Nothing Then Info.VersionCode = CInt(Val(Json("versionCode").ToString))
        If Json("url") IsNot Nothing Then Info.Url = Json("url").ToString.Trim
        If Json("notes") IsNot Nothing Then Info.Notes = Json("notes").ToString
        Return Info
    End Function

#End Region

End Module

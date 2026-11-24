Imports System.IO

''' <summary>一次社区列表同步的结果。</summary>
Public Class CommunitySyncResult
    Public Ok As Boolean = False
    ''' <summary>是否真的更新了本地列表。</summary>
    Public Updated As Boolean = False
    ''' <summary>给用户看的说明。</summary>
    Public Message As String = ""
    Public Revision As Integer = 0
    Public SourceUrl As String = ""
End Class

''' <summary>
''' 社区服务器列表的远程同步。
''' <para>
'''     数据源为托管在对象存储（腾讯云 COS）上的 servers.json；
'''     客户端按 revision 判断是否需要更新，校验通过后原子替换本地缓存。
''' </para>
''' </summary>
Public Module ModCommunitySync

#Region "配置"

    ''' <summary>
    ''' 社区服务器列表的默认远程地址（部署时改成自己的 COS 地址）。
    ''' 可配置多个作为备用源，按顺序尝试。
    ''' </summary>
    Public ReadOnly DefaultSourceUrls As String() = {
        "https://limlinlauncher-1254351530.cos.ap-shanghai.myqcloud.com/launcher/community/servers.json"
    }

    ''' <summary>自动检查的间隔（分钟）。</summary>
    Private Const SyncTtlMinutes As Integer = 60
    ''' <summary>客户端支持的最高 schemaVersion。</summary>
    Private Const SupportedSchemaVersion As Integer = 1

#End Region

#Region "路径与元数据"

    Private Function GetMetaPath() As String
        Return ModCommunity.GetCommunityFolder() & "meta.json"
    End Function

    Private Function GetSourceOverridePath() As String
        Return ModCommunity.GetCommunityFolder() & "source.json"
    End Function

    Private Function ReadMeta() As JObject
        Try
            If Not FileUtils.Exists(GetMetaPath()) Then Return New JObject()
            Dim Token As JToken = FileUtils.ReadAsJson(GetMetaPath())
            If Token IsNot Nothing AndAlso TypeOf Token Is JObject Then Return CType(Token, JObject)
        Catch ex As Exception
            Logger.Warn(ex, "读取社区列表同步状态失败")
        End Try
        Return New JObject()
    End Function

    Private Sub WriteMeta(Revision As Integer, SourceUrl As String)
        Try
            Dim Json As New JObject From {
                {"lastCheck", Date.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")},
                {"revision", Revision},
                {"sourceUrl", If(SourceUrl, "")}
            }
            FileUtils.WriteAtomic(GetMetaPath(), Json.ToString())
        Catch ex As Exception
            Logger.Warn(ex, "写入社区列表同步状态失败")
        End Try
    End Sub

    ''' <summary>上次检查时间；从未检查过则为 Nothing。</summary>
    Public Function GetLastCheck() As Date?
        Dim Value As Date
        Dim Meta As JObject = ReadMeta()
        If Meta("lastCheck") IsNot Nothing AndAlso Date.TryParse(Meta("lastCheck").ToString(), Value) Then Return Value
        Return Nothing
    End Function

    ''' <summary>本地列表的 revision。</summary>
    Public Function GetLocalRevision() As Integer
        Try
            If Not FileUtils.Exists(ModCommunity.GetServersFilePath()) Then Return 0
            Dim Json As JToken = FileUtils.ReadAsJson(ModCommunity.GetServersFilePath())
            If Json IsNot Nothing AndAlso Json("revision") IsNot Nothing Then Return CInt(Val(Json("revision").ToString()))
        Catch ex As Exception
            Logger.Warn(ex, "读取本地社区列表版本失败")
        End Try
        Return 0
    End Function

    ''' <summary>是否到了该自动检查的时间。</summary>
    Public Function IsSyncDue() As Boolean
        Dim Last As Date? = GetLastCheck()
        If Last Is Nothing Then Return True
        Return Date.Now - Last.Value >= TimeSpan.FromMinutes(SyncTtlMinutes)
    End Function

    ''' <summary>数据源列表（优先使用本地覆盖配置，便于测试）。</summary>
    Public Function GetSourceUrls() As List(Of String)
        Dim Result As New List(Of String)
        Try
            Dim OverridePath As String = GetSourceOverridePath()
            If FileUtils.Exists(OverridePath) Then
                Dim Json As JToken = FileUtils.ReadAsJson(OverridePath)
                If Json IsNot Nothing AndAlso Json("sourceUrls") IsNot Nothing Then
                    For Each Item As JToken In Json("sourceUrls")
                        Dim Url As String = Item.ToString().Trim()
                        If Url.Length > 0 Then Result.Add(Url)
                    Next
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取社区更新源配置失败")
        End Try
        If Result.Count = 0 Then Result.AddRange(DefaultSourceUrls)
        Return Result.Where(Function(u) Not String.IsNullOrWhiteSpace(u) AndAlso Not u.Contains("YOUR-BUCKET")).ToList()
    End Function

    ''' <summary>界面展示用的同步状态文本。</summary>
    Public Function GetStatusText() As String
        Dim Revision As Integer = GetLocalRevision()
        Dim Last As Date? = GetLastCheck()
        Dim LastText As String = If(Last Is Nothing, "从未检查", Last.Value.ToString("MM-dd HH:mm"))
        Dim SourceText As String = If(GetSourceUrls().Count = 0, "未配置远程源", "远程同步已启用")
        Return $"列表 revision {Revision} · {SourceText} · 上次检查 {LastText}"
    End Function

#End Region

#Region "同步"

    ''' <summary>
    ''' 尝试从远程源同步服务器列表。不会抛出异常。
    ''' </summary>
    ''' <param name="Force">True 表示忽略 TTL，立即检查（用户手动点击）。</param>
    Public Function TrySync(Force As Boolean) As CommunitySyncResult
        Dim Result As New CommunitySyncResult
        Dim Sources As List(Of String) = GetSourceUrls()
        If Sources.Count = 0 Then
            Result.Message = "尚未配置社区列表更新源"
            Return Result
        End If
        If Not Force AndAlso Not IsSyncDue() Then
            Result.Ok = True
            Result.Revision = GetLocalRevision()
            Result.Message = "距上次检查不足 1 小时，已跳过"
            Return Result
        End If

        For Each Url As String In Sources
            Try
                Logger.Info($"社区列表同步：请求 {Url}")
                Dim Content As String = NetRequestByClientRetry(Url, Headers:={{"Cache-Control", "no-cache"}})
                Content = Content.TrimStart(ChrW(&HFEFF), ChrW(&H200B)) '兼容带 BOM 的托管文件
                Dim Json As JObject = Content.DeserializeJson()
                Dim ValidateError As String = ValidateSnapshot(Json)
                If ValidateError IsNot Nothing Then
                    Logger.Warn($"社区列表同步：{Url} 校验失败：{ValidateError}")
                    Result.Message = "列表校验失败：" & ValidateError
                    Continue For
                End If
                Dim RemoteRevision As Integer = CInt(Val(Json("revision").ToString()))
                Dim LocalRevision As Integer = GetLocalRevision()
                Result.Revision = RemoteRevision
                Result.SourceUrl = Url
                If RemoteRevision < LocalRevision Then
                    Result.Ok = True
                    Result.Message = $"远程列表比本地旧（{RemoteRevision} < {LocalRevision}），已保留本地"
                    WriteMeta(LocalRevision, Url)
                    Return Result
                End If
                If RemoteRevision = LocalRevision Then
                    Result.Ok = True
                    Result.Message = "服务器列表已是最新"
                    WriteMeta(LocalRevision, Url)
                    Return Result
                End If
                FileUtils.WriteAtomic(ModCommunity.GetServersFilePath(), Content)
                WriteMeta(RemoteRevision, Url)
                Result.Ok = True
                Result.Updated = True
                Result.Message = $"已更新服务器列表（revision {RemoteRevision}）"
                Logger.Info($"社区列表同步成功：revision {RemoteRevision}，来源 {Url}")
                Return Result
            Catch ex As Exception
                Logger.Warn(ex, $"社区列表同步失败：{Url}")
                Result.Message = "同步失败：" & ex.Message
            End Try
        Next
        Return Result
    End Function

    ''' <summary>校验远程快照；合法返回 Nothing，否则返回错误描述。</summary>
    Private Function ValidateSnapshot(Json As JObject) As String
        If Json Is Nothing Then Return "内容不是 JSON 对象"
        If Json("servers") Is Nothing OrElse TypeOf Json("servers") IsNot JArray Then Return "缺少 servers 数组"
        Dim SchemaVersion As Integer = 1
        If Json("schemaVersion") IsNot Nothing Then SchemaVersion = CInt(Val(Json("schemaVersion").ToString()))
        If SchemaVersion > SupportedSchemaVersion Then Return $"列表格式版本过高（{SchemaVersion}），请升级启动器"
        If Json("minLauncherVersion") IsNot Nothing Then
            Dim MinVersion As String = Json("minLauncherVersion").ToString().Trim()
            If MinVersion.Length > 0 AndAlso Not IsVersionAtLeast(VersionBaseName, MinVersion) Then
                Return $"需要启动器 {MinVersion} 或更高版本"
            End If
        End If
        If Json("revision") Is Nothing Then Return "缺少 revision"
        Dim Revision As Integer = CInt(Val(Json("revision").ToString()))
        If Revision <= 0 Then Return "revision 必须为正整数"
        Dim SeenIds As New HashSet(Of String)
        For Each Item As JToken In Json("servers")
            If Item("id") Is Nothing OrElse String.IsNullOrWhiteSpace(Item("id").ToString()) Then Return "存在缺少 id 的服务器"
            Dim Id As String = Item("id").ToString().Trim()
            If Not SeenIds.Add(Id) Then Return $"服务器 id 重复：{Id}"
            If Item("ip") Is Nothing OrElse String.IsNullOrWhiteSpace(Item("ip").ToString()) Then Return $"服务器 {Id} 缺少 ip"
            If Item("port") IsNot Nothing Then
                Dim Port As Integer = CInt(Val(Item("port").ToString()))
                If Port <= 0 OrElse Port > 65535 Then Return $"服务器 {Id} 的端口非法"
            End If
        Next
        Return Nothing
    End Function

    ''' <summary>比较版本号（Current 是否 >= Required）。</summary>
    Private Function IsVersionAtLeast(Current As String, Required As String) As Boolean
        Try
            Dim CurrentParts As Integer() = Current.Split("."c).Select(Function(s) CInt(Val(s))).ToArray()
            Dim RequiredParts As Integer() = Required.Split("."c).Select(Function(s) CInt(Val(s))).ToArray()
            For Index As Integer = 0 To Math.Max(CurrentParts.Length, RequiredParts.Length) - 1
                Dim A As Integer = If(Index < CurrentParts.Length, CurrentParts(Index), 0)
                Dim B As Integer = If(Index < RequiredParts.Length, RequiredParts(Index), 0)
                If A > B Then Return True
                If A < B Then Return False
            Next
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "比较社区列表最低版本失败，按通过处理")
            Return True
        End Try
    End Function

#End Region

End Module

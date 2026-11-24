Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Runtime.InteropServices
Imports System.Threading

''' <summary>
''' 社区游戏模块：社区服务器列表的读取、状态查询（Server List Ping）与 MOTD 解析。
''' <para>
'''     服务器列表由管理员直接编辑 <c>PCL\Community\servers.json</c>；
'''     若文件不存在，启动时会自动生成一份模板。
''' </para>
''' </summary>
Public Module ModCommunity

#Region "配置"

    ''' <summary>社区数据目录。</summary>
    Public Function GetCommunityFolder() As String
        Return Paths.Base & "PCL\Community\"
    End Function

    ''' <summary>服务器列表文件路径。</summary>
    Public Function GetServersFilePath() As String
        Return GetCommunityFolder() & "servers.json"
    End Function

    ''' <summary>模组协议版本号（状态查询不严格校验版本，任意合法值均可）。</summary>
    Private Const SLPProtocolVersion As Integer = 47
    ''' <summary>连接超时（毫秒）。</summary>
    Private Const ConnectTimeout As Integer = 6000
    ''' <summary>读取超时（毫秒）。</summary>
    Private Const ReadTimeout As Integer = 6000
    ''' <summary>单次刷新的最大并发查询数，避免把服务器打挂。</summary>
    Public Const MaxConcurrentQueries As Integer = 4

#End Region

#Region "数据模型"

    ''' <summary>社区服务器条目（对应 servers.json 中的一个对象）。</summary>
    Public Class CommunityServer
        Public Id As String = ""
        Public Name As String = ""
        ''' <summary>分组，如「生存」「创造」。</summary>
        Public Group As String = ""
        Public Ip As String = ""
        Public Port As Integer = 25565
        ''' <summary>管理员手写的游玩注意，可多行。</summary>
        Public Notes As String = ""
        ''' <summary>排序权重，越小越靠前。</summary>
        Public Order As Integer = 0
        Public Enabled As Boolean = True
        ''' <summary>是否展示玩家名单。</summary>
        Public ShowPlayers As Boolean = True
        ''' <summary>查询失败时展示的静态 MOTD（可选）。</summary>
        Public MotdOverride As String = ""
        ''' <summary>建议的 Minecraft 版本（展示用，为一键加入预留）。</summary>
        Public GameVersion As String = ""
        ''' <summary>建议的 Mod 加载器（展示用，为一键加入预留）。</summary>
        Public GameLoader As String = ""
        ''' <summary>是否通过 _minecraft._tcp SRV 记录解析真实地址（用于隐藏端口）。</summary>
        Public UseSrv As Boolean = False

        ''' <summary>对外展示的地址：使用 SRV 时只显示域名。</summary>
        Public ReadOnly Property DisplayAddress As String
            Get
                If UseSrv Then Return Ip
                Return If(Port = 25565, Ip, $"{Ip}:{Port}")
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    ''' <summary>MOTD 的一个样式片段。</summary>
    Public Class MotdPart
        Public Text As String = ""
        ''' <summary>颜色，形如 "FF5555"；为 Nothing 表示默认颜色。</summary>
        Public ColorHex As String = Nothing
        Public Bold As Boolean = False
        Public Italic As Boolean = False
        Public Underline As Boolean = False
        Public Strike As Boolean = False
    End Class

    ''' <summary>服务器状态查询结果。</summary>
    Public Class CommunityServerStatus
        Public Ok As Boolean = False
        ''' <summary>失败原因（Ok=False 时有效）。</summary>
        Public Message As String = ""
        Public Online As Integer = -1
        Public Max As Integer = -1
        ''' <summary>玩家名单（来自服务器返回的 sample）。</summary>
        Public Players As New List(Of String)
        Public VersionName As String = ""
        ''' <summary>查询耗时（毫秒）。</summary>
        Public Latency As Long = -1
        ''' <summary>MOTD，按行组织。</summary>
        Public Motd As New List(Of List(Of MotdPart))
        ''' <summary>SRV 解析出的真实地址（未使用 SRV 时为 Nothing）。</summary>
        Public ResolvedAddress As String = Nothing
        Public CheckedAt As Date = Date.Now
    End Class

#End Region

#Region "服务器列表读写"

    ''' <summary>
    ''' 确保服务器列表文件存在（不存在则写入模板），并返回其内容。
    ''' </summary>
    Public Sub EnsureServersFile()
        Try
            DirectoryUtils.Create(GetCommunityFolder())
            If FileUtils.Exists(GetServersFilePath()) Then Return
            Dim Root As New JObject From {
                {"schemaVersion", 1},
                {"revision", 1},
                {"updatedAt", Date.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")},
                {"servers", BuildDefaultServersJson()}
            }
            FileUtils.Write(GetServersFilePath(), Root.ToString())
            Logger.Info($"已生成社区服务器列表模板：{GetServersFilePath()}")
        Catch ex As Exception
            Logger.Warn(ex, "生成社区服务器列表模板失败")
        End Try
    End Sub

    ''' <summary>读取服务器列表；失败时回退到内置默认列表。</summary>
    Public Function LoadServers() As List(Of CommunityServer)
        EnsureServersFile()
        Try
            If Not FileUtils.Exists(GetServersFilePath()) Then Return GetDefaultServers()
            Dim Json As JToken = FileUtils.ReadAsJson(GetServersFilePath())
            If Json Is Nothing OrElse Json("servers") Is Nothing Then Return GetDefaultServers()
            Dim Result As New List(Of CommunityServer)
            For Each Item As JToken In Json("servers")
                Try
                    Dim Server As New CommunityServer
                    Server.Id = GetJsonString(Item, "id")
                    Server.Name = GetJsonString(Item, "name")
                    Server.Group = GetJsonString(Item, "group")
                    Server.Ip = GetJsonString(Item, "ip")
                    Server.UseSrv = GetJsonBoolean(Item, "useSrv", False)
                    Dim RawPort As Integer = GetJsonInteger(Item, "port", 0)
                    If RawPort > 0 Then
                        Server.Port = RawPort
                    Else
                        Server.Port = 25565
                        If Item("useSrv") Is Nothing Then Server.UseSrv = True '未写端口且未显式关闭 SRV 时，默认走 SRV
                    End If
                    If Server.UseSrv AndAlso Server.Port <> 25565 Then Server.UseSrv = False '显式写了端口则不使用 SRV
                    Server.Notes = GetJsonString(Item, "notes")
                    Server.Order = GetJsonInteger(Item, "order", 0)
                    Server.Enabled = GetJsonBoolean(Item, "enabled", True)
                    Server.ShowPlayers = GetJsonBoolean(Item, "showPlayers", True)
                    Server.MotdOverride = GetJsonString(Item, "motdOverride")
                    Server.GameVersion = GetJsonString(Item, "gameVersion")
                    Server.GameLoader = GetJsonString(Item, "gameLoader")
                    If String.IsNullOrWhiteSpace(Server.Id) OrElse String.IsNullOrWhiteSpace(Server.Ip) Then
                        Logger.Warn($"社区服务器列表中有条目缺少 id 或 ip，已跳过：{Item}")
                        Continue For
                    End If
                    If String.IsNullOrWhiteSpace(Server.Name) Then Server.Name = Server.Ip
                    Result.Add(Server)
                Catch ex As Exception
                    Logger.Warn(ex, "解析社区服务器条目失败")
                End Try
            Next
            If Result.Count = 0 Then Return GetDefaultServers()
            Return Result.OrderBy(Function(s) s.Order).ToList()
        Catch ex As Exception
            Logger.Warn(ex, "读取社区服务器列表失败，使用内置默认列表")
            Return GetDefaultServers()
        End Try
    End Function

    Private Function GetJsonString(Item As JToken, Key As String) As String
        Return If(Item(Key) Is Nothing, "", Item(Key).ToString().Trim())
    End Function

    Private Function GetJsonInteger(Item As JToken, Key As String, DefaultValue As Integer) As Integer
        If Item(Key) Is Nothing Then Return DefaultValue
        Return CInt(Val(Item(Key).ToString()))
    End Function

    Private Function GetJsonBoolean(Item As JToken, Key As String, DefaultValue As Boolean) As Boolean
        If Item(Key) Is Nothing Then Return DefaultValue
        Try
            Return Convert.ToBoolean(Item(Key).ToString())
        Catch
            Return DefaultValue
        End Try
    End Function

    ''' <summary>内置默认列表（模板使用，管理员替换为真实服务器即可）。</summary>
    Public Function GetDefaultServers() As List(Of CommunityServer)
        Return New List(Of CommunityServer) From {
            New CommunityServer With {
                .Id = "limlin-main", .Name = "栎木主世界", .Group = "生存", .Order = 10,
                .Ip = "mc1.example.com", .Port = 25565,
                .Notes = "示例数据：请编辑 PCL\Community\servers.json，把 ip、名称、注意事项改为真实信息。",
                .GameVersion = "1.20.1", .GameLoader = "Forge"
            },
            New CommunityServer With {
                .Id = "limlin-creative", .Name = "栎木创造服", .Group = "创造", .Order = 20,
                .Ip = "mc2.example.com", .Port = 25565,
                .Notes = "示例数据：这是第二条示例，同样可自由增删。",
                .GameVersion = "1.20.1", .GameLoader = "Fabric"
            }
        }
    End Function

    Private Function BuildDefaultServersJson() As JArray
        Dim Array As New JArray
        For Each Server In GetDefaultServers()
            Array.Add(New JObject From {
                {"id", Server.Id},
                {"name", Server.Name},
                {"group", Server.Group},
                {"ip", Server.Ip},
                {"port", Server.Port},
                {"order", Server.Order},
                {"enabled", Server.Enabled},
                {"showPlayers", Server.ShowPlayers},
                {"notes", Server.Notes},
                {"gameVersion", Server.GameVersion},
                {"gameLoader", Server.GameLoader},
                {"motdOverride", ""}
            })
        Next
        Return Array
    End Function

#End Region

#Region "状态查询"

    ''' <summary>
    ''' 查询单个服务器状态（Minecraft Server List Ping）。不会抛出异常。
    ''' </summary>
    Public Function QueryServer(Server As CommunityServer) As CommunityServerStatus
        Dim Result As New CommunityServerStatus With {.CheckedAt = Date.Now}
        Dim Host As String = Server.Ip.Trim()
        Dim Port As Integer = Server.Port
        '支持在 ip 字段里直接写 host:port 或 [IPv6]:port
        If Host.StartsWith("[") Then
            Dim Index As Integer = Host.IndexOf("]")
            If Index > 0 Then
                Dim Tail As String = Host.Substring(Index + 1).TrimStart(":")
                Host = Host.Substring(1, Index - 1)
                If Tail.Length > 0 Then Port = CInt(Val(Tail))
            End If
        ElseIf Host.Count(Function(c) c = ":"c) = 1 Then
            Dim Parts As String() = Host.Split(":"c)
            Host = Parts(0)
            Port = CInt(Val(Parts(1)))
        End If
        If Port <= 0 OrElse Port > 65535 Then Port = 25565
        '使用 SRV 记录解析真实地址（用于隐藏端口）
        Dim ResolvedAddress As String = Nothing
        If Server.UseSrv Then
            Dim SrvRecord As SrvRecord = ResolveSrv(Host)
            If SrvRecord IsNot Nothing Then
                ResolvedAddress = $"{SrvRecord.Target}:{SrvRecord.Port}"
                Host = SrvRecord.Target
                Port = SrvRecord.Port
            Else
                Logger.Info($"SRV 解析无结果，按默认端口连接：{Host}")
            End If
        End If

        Dim LastException As Exception = Nothing
        For Attempt As Integer = 1 To 2
            Dim Stopwatch As Diagnostics.Stopwatch = Diagnostics.Stopwatch.StartNew()
            Try
                Using Client As New TcpClient()
                    Dim AsyncResult As IAsyncResult = Client.BeginConnect(Host, Port, Nothing, Nothing)
                    If Not AsyncResult.AsyncWaitHandle.WaitOne(ConnectTimeout) Then Throw New TimeoutException("连接超时")
                    Client.EndConnect(AsyncResult)
                    Client.ReceiveTimeout = ReadTimeout
                    Dim Stream As NetworkStream = Client.GetStream()
                    '握手包
                    Dim Handshake As New MemoryStream()
                    WriteVarInt(Handshake, 0) '包 ID
                    WriteVarInt(Handshake, SLPProtocolVersion)
                    WriteString(Handshake, Host)
                    WriteUShort(Handshake, Port)
                    WriteVarInt(Handshake, 1) '请求状态
                    WriteVarInt(Stream, CInt(Handshake.Length))
                    Handshake.Position = 0
                    Handshake.CopyTo(Stream)
                    '状态请求包
                    Dim Request As New MemoryStream()
                    WriteVarInt(Request, 0)
                    WriteVarInt(Stream, CInt(Request.Length))
                    Request.Position = 0
                    Request.CopyTo(Stream)
                    Stream.Flush()
                    '读取响应
                    ReadVarInt(Stream) '包长度
                    ReadVarInt(Stream) '包 ID
                    Dim JsonLength As Integer = ReadVarInt(Stream)
                    If JsonLength <= 0 OrElse JsonLength > 4 * 1024 * 1024 Then Throw New Exception("响应长度异常")
                    Dim Buffer(JsonLength - 1) As Byte
                    ReadExactly(Stream, Buffer)
                    Dim Json As JObject = Encoding.UTF8.GetString(Buffer).DeserializeJson()
                    Stopwatch.Stop()
                    Result.Latency = Stopwatch.ElapsedMilliseconds
                    Result.ResolvedAddress = ResolvedAddress
                    If Json Is Nothing Then Throw New Exception("响应内容为空")
                    ParseStatusJson(Json, Result)
                    Result.Ok = True
                    Return Result
                End Using
            Catch ex As Exception
                Stopwatch.Stop()
                LastException = ex
                If Attempt >= 2 Then Exit For
                Thread.Sleep(300 + RandomInteger(0, 400))
            End Try
        Next
        Result.Message = If(LastException Is Nothing, "未知错误", FriendlyError(LastException))
        Return Result
    End Function

    Private Function FriendlyError(Ex As Exception) As String
        If TypeOf Ex Is TimeoutException OrElse Ex.Message.Contains("超时") Then Return "连接超时"
        If TypeOf Ex Is SocketException Then
            Dim SocketEx As SocketException = CType(Ex, SocketException)
            Select Case SocketEx.SocketErrorCode
                Case SocketError.ConnectionRefused : Return "服务器拒绝连接（可能已关闭）"
                Case SocketError.HostNotFound : Return "找不到服务器地址"
                Case SocketError.TimedOut : Return "连接超时"
                Case Else : Return $"网络错误（{SocketEx.SocketErrorCode}）"
            End Select
        End If
        Return Ex.Message
    End Function

    ''' <summary>批量查询（并发受限），返回 id → 状态。</summary>
    Public Function QueryServers(Servers As List(Of CommunityServer)) As Dictionary(Of String, CommunityServerStatus)
        Dim Results As New ConcurrentDictionary(Of String, CommunityServerStatus)
        Dim Semaphore As New SemaphoreSlim(MaxConcurrentQueries, MaxConcurrentQueries)
        Dim Tasks As Threading.Tasks.Task() = Servers.Select(
            Function(Server) Threading.Tasks.Task.Run(
                Sub()
                    Semaphore.Wait()
                    Try
                        Results(Server.Id) = QueryServer(Server)
                    Finally
                        Semaphore.Release()
                    End Try
                End Sub)).ToArray()
        Threading.Tasks.Task.WaitAll(Tasks)
        Dim Output As New Dictionary(Of String, CommunityServerStatus)
        For Each Pair In Results
            Output(Pair.Key) = Pair.Value
        Next
        Return Output
    End Function

    Private Sub ParseStatusJson(Json As JObject, Result As CommunityServerStatus)
        '版本
        If Json("version") IsNot Nothing AndAlso Json("version")("name") IsNot Nothing Then
            Result.VersionName = Json("version")("name").ToString().Trim()
        End If
        '人数与玩家列表
        If Json("players") IsNot Nothing Then
            Result.Online = CInt(Val(If(Json("players")("online") Is Nothing, "-1", Json("players")("online").ToString())))
            Result.Max = CInt(Val(If(Json("players")("max") Is Nothing, "-1", Json("players")("max").ToString())))
            If Json("players")("sample") IsNot Nothing Then
                For Each Player As JToken In Json("players")("sample")
                    Try
                        If Player("name") IsNot Nothing Then Result.Players.Add(Player("name").ToString().Trim())
                    Catch
                        '忽略无法解析的玩家项
                    End Try
                Next
            End If
        End If
        'MOTD
        Result.Motd = ParseMotd(Json("description"))
    End Sub

#End Region

#Region "MOTD 解析"

    ''' <summary>解析 Minecraft 聊天组件为纯文本，再按行/样式切分。</summary>
    Public Function ParseMotd(Description As JToken) As List(Of List(Of MotdPart))
        Return ParseMotdText(FlattenChat(Description))
    End Function

    ''' <summary>把聊天组件（字符串、对象或数组）展开为纯文本。</summary>
    Private Function FlattenChat(Token As JToken) As String
        If Token Is Nothing Then Return ""
        If Token.Type = JTokenType.String OrElse Token.Type = JTokenType.Integer OrElse Token.Type = JTokenType.Boolean Then
            Return Token.ToString()
        End If
        Dim Builder As New StringBuilder()
        If Token.Type = JTokenType.Array Then
            For Each Child As JToken In Token
                Builder.Append(FlattenChat(Child))
            Next
            Return Builder.ToString()
        End If
        If Token("text") IsNot Nothing Then Builder.Append(Token("text").ToString())
        If Token("translate") IsNot Nothing AndAlso Builder.Length = 0 Then Builder.Append(Token("translate").ToString())
        If Token("extra") IsNot Nothing Then
            For Each Child As JToken In Token("extra")
                Builder.Append(FlattenChat(Child))
            Next
        End If
        Return Builder.ToString()
    End Function

    ''' <summary>解析带 § 颜色码的 MOTD 文本。</summary>
    Public Function ParseMotdText(Text As String) As List(Of List(Of MotdPart))
        Dim Lines As New List(Of List(Of MotdPart))
        Dim CurrentLine As New List(Of MotdPart)
        Dim Style As New MotdPart()
        Dim Buffer As New StringBuilder()

        Dim Flush As Action =
        Sub()
            If Buffer.Length = 0 Then Return
            CurrentLine.Add(New MotdPart With {
                .Text = Buffer.ToString(),
                .ColorHex = Style.ColorHex,
                .Bold = Style.Bold,
                .Italic = Style.Italic,
                .Underline = Style.Underline,
                .Strike = Style.Strike
            })
            Buffer.Clear()
        End Sub

        If String.IsNullOrEmpty(Text) Then
            Lines.Add(New List(Of MotdPart) From {New MotdPart With {.Text = ""}})
            Return Lines
        End If

        Dim Index As Integer = 0
        While Index < Text.Length
            Dim Ch As Char = Text(Index)
            If Ch = vbLf Then
                Flush.Invoke()
                Lines.Add(CurrentLine)
                CurrentLine = New List(Of MotdPart)
                Index += 1
                Continue While
            End If
            If Ch = vbCr Then
                Index += 1
                Continue While
            End If
            If Ch = "§"c AndAlso Index + 1 < Text.Length Then
                Flush.Invoke()
                Dim Code As Char = Text(Index + 1).ToString().ToLowerInvariant()(0)
                Index += 2
                '十六进制颜色：§#RRGGBB
                If Code = "#"c AndAlso Index + 6 <= Text.Length Then
                    Dim Hex As String = Text.Substring(Index, 6)
                    If IsHexColor(Hex) Then
                        Style.ColorHex = Hex
                        Index += 6
                    End If
                    Continue While
                End If
                Select Case Code
                    Case "0"c, "1"c, "2"c, "3"c, "4"c, "5"c, "6"c, "7"c,
                         "8"c, "9"c, "a"c, "b"c, "c"c, "d"c, "e"c, "f"c
                        '颜色码会重置样式
                        Style = New MotdPart With {.ColorHex = LegacyColor(Code)}
                    Case "l"c : Style.Bold = True
                    Case "o"c : Style.Italic = True
                    Case "n"c : Style.Underline = True
                    Case "m"c : Style.Strike = True
                    Case "r"c : Style = New MotdPart()
                    Case Else '§k 等无法静态渲染的格式直接忽略
                End Select
                Continue While
            End If
            Buffer.Append(Ch)
            Index += 1
        End While
        Flush.Invoke()
        Lines.Add(CurrentLine)
        Return Lines
    End Function

    Private Function IsHexColor(Hex As String) As Boolean
        For Each Ch As Char In Hex
            If Not ((Ch >= "0"c AndAlso Ch <= "9"c) OrElse
                    (Ch >= "a"c AndAlso Ch <= "f"c) OrElse
                    (Ch >= "A"c AndAlso Ch <= "F"c)) Then Return False
        Next
        Return True
    End Function

    Private Function LegacyColor(Code As Char) As String
        Select Case Code
            Case "0"c : Return "000000"
            Case "1"c : Return "0000AA"
            Case "2"c : Return "00AA00"
            Case "3"c : Return "00AAAA"
            Case "4"c : Return "AA0000"
            Case "5"c : Return "AA00AA"
            Case "6"c : Return "FFAA00"
            Case "7"c : Return "AAAAAA"
            Case "8"c : Return "555555"
            Case "9"c : Return "5555FF"
            Case "a"c : Return "55FF55"
            Case "b"c : Return "55FFFF"
            Case "c"c : Return "FF5555"
            Case "d"c : Return "FF55FF"
            Case "e"c : Return "FFFF55"
            Case "f"c : Return "FFFFFF"
        End Select
        Return Nothing
    End Function

#End Region

#Region "协议工具"

    Private Sub WriteVarInt(Stream As Stream, Value As Integer)
        Dim Current As UInteger = CUInt(Value)
        Dim Buffer(4) As Byte
        Dim Index As Integer = 0
        Do
            Dim ByteValue As Byte = CUIntToByte(Current And &H7FUI)
            Current = Current >> 7
            If Current <> 0 Then ByteValue = ByteValue Or &H80
            Buffer(Index) = ByteValue
            Index += 1
        Loop While Current <> 0
        Stream.Write(Buffer, 0, Index)
    End Sub

    Private Function CUIntToByte(Value As UInteger) As Byte
        Return CByte(Value And &HFFUI)
    End Function

    Private Sub WriteString(Stream As Stream, Value As String)
        Dim Bytes As Byte() = Encoding.UTF8.GetBytes(Value)
        WriteVarInt(Stream, Bytes.Length)
        Stream.Write(Bytes, 0, Bytes.Length)
    End Sub

    Private Sub WriteUShort(Stream As Stream, Value As Integer)
        Stream.WriteByte(CByte((Value >> 8) And &HFF))
        Stream.WriteByte(CByte(Value And &HFF))
    End Sub

    Private Function ReadVarInt(Stream As Stream) As Integer
        Dim Result As Integer = 0
        Dim Shift As Integer = 0
        Do
            Dim ByteValue As Integer = Stream.ReadByte()
            If ByteValue < 0 Then Throw New IOException("连接已关闭")
            Result = Result Or ((ByteValue And &H7F) << Shift)
            If (ByteValue And &H80) = 0 Then Exit Do
            Shift += 7
            If Shift > 35 Then Throw New Exception("VarInt 过长")
        Loop
        Return Result
    End Function

    Private Sub ReadExactly(Stream As Stream, Buffer As Byte())
        Dim Offset As Integer = 0
        While Offset < Buffer.Length
            Dim Count As Integer = Stream.Read(Buffer, Offset, Buffer.Length - Offset)
            If Count <= 0 Then Throw New IOException("连接已关闭")
            Offset += Count
        End While
    End Sub

#End Region

#Region "SRV 解析"

    ''' <summary>一条 SRV 记录。</summary>
    Private Class SrvRecord
        Public Target As String = ""
        Public Port As Integer = 25565
        Public Priority As Integer = 0
        Public Weight As Integer = 0
    End Class

    ''' <summary>
    ''' 查询 _minecraft._tcp.&lt;域名&gt; 的 SRV 记录。失败或无记录时返回 Nothing。
    ''' <para>使用自实现的 UDP DNS 查询，避免 DnsQuery 结构体在不同系统/位数下的布局差异。</para>
    ''' </summary>
    Private Function ResolveSrv(Name As String) As SrvRecord
        Dim QueryName As String = "_minecraft._tcp." & Name.Trim().TrimEnd("."c)
        For Each Server As System.Net.IPAddress In GetDnsServers()
            Try
                Dim Response As Byte() = QueryDnsUdp(Server, QueryName, 33)
                Dim Records As List(Of SrvRecord) = ParseSrvRecords(Response)
                If Records.Count = 0 Then Continue For
                Dim Best As SrvRecord = Nothing
                For Each Record As SrvRecord In Records
                    If Best Is Nothing OrElse Record.Priority < Best.Priority OrElse
                       (Record.Priority = Best.Priority AndAlso Record.Weight > Best.Weight) Then Best = Record
                Next
                If Best IsNot Nothing Then
                    Logger.Info($"SRV 解析：{Name} → {Best.Target}:{Best.Port}（priority {Best.Priority} weight {Best.Weight}，DNS {Server}）")
                    Return Best
                End If
            Catch ex As Exception
                Logger.Info($"SRV 查询失败（DNS {Server}）：{ex.Message}")
            End Try
        Next
        Logger.Info($"SRV 查询无结果：{QueryName}")
        Return Nothing
    End Function

    ''' <summary>获取系统配置的 DNS 服务器（IPv4），失败时使用国内公共 DNS。</summary>
    Private Function GetDnsServers() As List(Of System.Net.IPAddress)
        Dim Result As New List(Of System.Net.IPAddress)
        Try
            For Each Adapter As System.Net.NetworkInformation.NetworkInterface In System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If Adapter.OperationalStatus <> System.Net.NetworkInformation.OperationalStatus.Up Then Continue For
                For Each Address As System.Net.IPAddress In Adapter.GetIPProperties().DnsAddresses
                    If Address.AddressFamily = System.Net.Sockets.AddressFamily.InterNetwork AndAlso Not Result.Contains(Address) Then Result.Add(Address)
                Next
            Next
        Catch ex As Exception
            Logger.Warn(ex, "获取系统 DNS 服务器失败")
        End Try
        If Result.Count = 0 Then
            Result.Add(System.Net.IPAddress.Parse("223.5.5.5"))
            Result.Add(System.Net.IPAddress.Parse("119.29.29.29"))
        End If
        Return Result
    End Function

    ''' <summary>发送一次 UDP DNS 查询并返回原始响应。</summary>
    Private Function QueryDnsUdp(Server As System.Net.IPAddress, QueryName As String, RecordType As Integer) As Byte()
        Dim Packet As Byte() = BuildDnsQuery(QueryName, RecordType)
        Using Client As New System.Net.Sockets.UdpClient()
            Client.Client.SendTimeout = 2500
            Client.Client.ReceiveTimeout = 2500
            Client.Connect(Server, 53)
            Client.Send(Packet, Packet.Length)
            Dim Remote As New System.Net.IPEndPoint(System.Net.IPAddress.Any, 0)
            Return Client.Receive(Remote)
        End Using
    End Function

    ''' <summary>构造 SRV 查询报文（标准查询，递归请求）。</summary>
    Private Function BuildDnsQuery(QueryName As String, RecordType As Integer) As Byte()
        Using Stream As New MemoryStream()
            WriteUInt16(Stream, RandomInteger(0, 65535)) '事务 ID
            WriteUInt16(Stream, &H100) '标准查询 + 递归请求
            WriteUInt16(Stream, 1) 'QDCOUNT
            WriteUInt16(Stream, 0) 'ANCOUNT
            WriteUInt16(Stream, 0) 'NSCOUNT
            WriteUInt16(Stream, 0) 'ARCOUNT
            For Each Label As String In QueryName.Split("."c)
                Dim Bytes As Byte() = System.Text.Encoding.ASCII.GetBytes(Label)
                Stream.WriteByte(CByte(Bytes.Length))
                Stream.Write(Bytes, 0, Bytes.Length)
            Next
            Stream.WriteByte(0)
            WriteUInt16(Stream, RecordType)
            WriteUInt16(Stream, 1) 'IN
            Return Stream.ToArray()
        End Using
    End Function

    ''' <summary>解析 DNS 响应中的 SRV 记录。</summary>
    Private Function ParseSrvRecords(Data As Byte()) As List(Of SrvRecord)
        Dim Result As New List(Of SrvRecord)
        If Data Is Nothing OrElse Data.Length < 12 Then Return Result
        If (Data(3) And &H0F) <> 0 Then Return Result 'RCODE 非 0 视为失败
        Dim QuestionCount As Integer = ReadUInt16(Data, 4)
        Dim AnswerCount As Integer = ReadUInt16(Data, 6)
        Dim Offset As Integer = 12
        For Index As Integer = 1 To QuestionCount
            Dim ReadOffset As Integer = Offset
            ReadDnsName(Data, ReadOffset)
            Offset = ReadOffset + 4
        Next
        For Index As Integer = 1 To AnswerCount
            If Offset + 10 > Data.Length Then Exit For
            Dim ReadOffset As Integer = Offset
            ReadDnsName(Data, ReadOffset)
            Offset = ReadOffset
            Dim RecordType As Integer = ReadUInt16(Data, Offset)
            Offset += 2
            Offset += 2 'CLASS
            Offset += 4 'TTL
            Dim DataLength As Integer = ReadUInt16(Data, Offset)
            Offset += 2
            If Offset + DataLength > Data.Length Then Exit For
            If RecordType = 33 AndAlso DataLength >= 7 Then
                Dim Priority As Integer = ReadUInt16(Data, Offset)
                Dim Weight As Integer = ReadUInt16(Data, Offset + 2)
                Dim Port As Integer = ReadUInt16(Data, Offset + 4)
                Dim TargetOffset As Integer = Offset + 6
                Dim Target As String = ReadDnsName(Data, TargetOffset).TrimEnd("."c)
                If Target.Length > 0 Then
                    Result.Add(New SrvRecord With {
                        .Target = Target,
                        .Port = If(Port > 0, Port, 25565),
                        .Priority = Priority,
                        .Weight = Weight
                    })
                End If
            End If
            Offset += DataLength
        Next
        Return Result
    End Function

    ''' <summary>读取 DNS 域名（支持压缩指针）。</summary>
    Private Function ReadDnsName(Data As Byte(), ByRef Offset As Integer) As String
        Dim Parts As New List(Of String)
        Dim Position As Integer = Offset
        Dim Jumped As Boolean = False
        Dim Guard As Integer = 0
        While Position < Data.Length
            Guard += 1
            If Guard > 128 Then Exit While
            Dim Length As Integer = Data(Position)
            If (Length And &HC0) = &HC0 Then
                If Position + 1 >= Data.Length Then Exit While
                Dim Pointer As Integer = ((Length And &H3F) << 8) Or Data(Position + 1)
                If Not Jumped Then
                    Offset = Position + 2
                    Jumped = True
                End If
                Position = Pointer
            ElseIf Length = 0 Then
                If Not Jumped Then Offset = Position + 1
                Exit While
            Else
                Position += 1
                If Position + Length > Data.Length Then Exit While
                Parts.Add(System.Text.Encoding.ASCII.GetString(Data, Position, Length))
                Position += Length
            End If
        End While
        Return String.Join(".", Parts)
    End Function

    Private Sub WriteUInt16(Stream As Stream, Value As Integer)
        Stream.WriteByte(CByte((Value >> 8) And &HFF))
        Stream.WriteByte(CByte(Value And &HFF))
    End Sub

    Private Function ReadUInt16(Data As Byte(), Offset As Integer) As Integer
        If Offset < 0 OrElse Offset + 1 >= Data.Length Then Return 0
        Return (CInt(Data(Offset)) << 8) Or CInt(Data(Offset + 1))
    End Function

#End Region

End Module

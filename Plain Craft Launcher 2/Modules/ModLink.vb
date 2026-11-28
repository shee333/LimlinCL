Imports System.IO
Imports Limlin.Link

''' <summary>启动器与联机库（Limlin.Link）之间的桥接层。</summary>
Public Module ModLink

    ''' <summary>当前联机会话，未联机时为 Nothing。</summary>
    Public Session As LinkSession = Nothing
    Private SessionSerial As Integer = 0

    Public Event SessionProgress(Text As String)
    Public Event SessionInvite(Code As String)
    Public Event SessionFailed(Message As String)
    Public Event SessionEnded()

    ''' <summary>联机库是否可用。</summary>
    Public ReadOnly Property IsAvailable As Boolean
        Get
            Try
                Return Not String.IsNullOrEmpty(LinkDiagnostics.Version())
            Catch ex As Exception
                Logger.Warn(ex, "[联机] 加载联机库失败")
                Return False
            End Try
        End Get
    End Property

    ''' <summary>当前是否处于联机房间中。</summary>
    Public ReadOnly Property IsRunning As Boolean
        Get
            Return Session IsNot Nothing AndAlso Session.IsRunning
        End Get
    End Property

    ''' <summary>当前是否为房主。</summary>
    Public ReadOnly Property IsHost As Boolean
        Get
            If Session Is Nothing Then Return False
            Return Session.Role = LinkRole.Host
        End Get
    End Property

    ''' <summary>创建房间；LanPort 为 0 时自动等待游戏「对局域网开放」。</summary>
    Public Function StartHost(Optional LanPort As Integer = 0) As Boolean
        StopSession()
        PrepareEnvironment()
        SessionSerial += 1
        Try
            Session = LinkSession.StartHost(LanPort, AddressOf ReportProgress)
            HookSession()
            Return True
        Catch ex As Exception
            Logger.Error(ex, "[联机] 创建会话失败")
            Return False
        End Try
    End Function

    ''' <summary>加入房间。</summary>
    Public Function JoinRoom(Invite As String) As Boolean
        StopSession()
        PrepareEnvironment()
        SessionSerial += 1
        Try
            Session = LinkSession.Join(Invite, AddressOf ReportProgress)
            HookSession()
            Return True
        Catch ex As Exception
            Logger.Error(ex, "[联机] 加入会话失败")
            Return False
        End Try
    End Function

    ''' <summary>关闭房间 / 离开房间。</summary>
    Public Sub StopSession()
        Dim Current As LinkSession = Session
        Session = Nothing
        SessionSerial += 1 '让旧会话的迟到事件失效
        If Current Is Nothing Then Return '没有会话时不要打扰界面（StartHost/JoinRoom 开头也会调用本方法）
        Try
            Current.Dispose()
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 关闭会话失败")
        End Try
        Try
            RunInUi(Sub() RaiseEvent SessionEnded())
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 通知界面失败")
        End Try
    End Sub

    ''' <summary>生成一个邀请码（供 UI 展示）。</summary>
    Public Function NewInviteCode() As String
        Return LinkDiagnostics.NewInviteCode()
    End Function

    ''' <summary>解析邀请码，返回可读描述。</summary>
    Public Function DescribeInvite(Text As String) As String
        Return LinkDiagnostics.DescribeInvite(Text)
    End Function

    ''' <summary>联机组件状态。</summary>
    Public Function ComponentState() As String
        Return LinkDiagnostics.ComponentState()
    End Function

    ' ========== 内部 ==========

    Private Sub HookSession()
        If Session Is Nothing Then Return
        Dim Current As LinkSession = Session
        Dim Serial As Integer = SessionSerial
        AddHandler Current.InviteCodeReady, Sub(Code As String) PostToUi(Serial, Sub() RaiseEvent SessionInvite(Code))
        AddHandler Current.Failed, Sub(Message As String) PostToUi(Serial, Sub() RaiseEvent SessionFailed(Message))
        AddHandler Current.LocalWorldReady, Sub(Port As Integer) PostToUi(Serial, Sub() RaiseEvent SessionProgress($"已加入房间，本地端口 {Port}，可在「多人游戏 → 局域网世界」中进入"))
    End Sub

    ''' <summary>仅当仍是同一代会话时才把事件送到界面，避免取消后的迟到事件干扰。</summary>
    Private Sub PostToUi(Serial As Integer, Action As Action)
        RunInUi(
            Sub()
                If Serial <> SessionSerial Then Return
                Action()
            End Sub)
    End Sub

    Private Sub ReportProgress(Text As String)
        Dim Serial As Integer = SessionSerial
        PostToUi(Serial, Sub() RaiseEvent SessionProgress(Text))
    End Sub

    ''' <summary>把启动器的账号与数据目录注入联机库。</summary>
    Private Sub PrepareEnvironment()
        Try
            LinkSettings.DataDirectory = Paths.Base & "PCL\Link"
            LinkSettings.EffectivePlayerName = GetPlayerName()
            LinkSettings.ShowSkins = False
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 初始化环境失败")
        End Try
    End Sub

    Private Function GetPlayerName() As String
        Try
            If Settings.Get(Of McLoginType)("LoginType") = McLoginType.Ms Then
                Dim MsJson As JObject = Settings.Get(Of String)("LoginMsJson").DeserializeJson()
                If MsJson IsNot Nothing AndAlso MsJson.Count > 0 Then Return MsJson.Properties().First().Name
            Else
                Dim Names As String() = Settings.Get(Of String)("LoginLegacyName").ToString.Split("¨"c)
                If Names.Length > 0 AndAlso Not String.IsNullOrWhiteSpace(Names(0)) Then Return Names(0)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 获取账号名失败")
        End Try
        Return "玩家"
    End Function

    ''' <summary>开发自检：由 PCL 数据目录下的 link-selftest.on 标记触发。</summary>
    Public Sub RunSelfTest()
        Try
            Logger.Info("[联机] ===== 自检开始 =====")
            Logger.Info("[联机] 库版本：" & LinkDiagnostics.Version())
            Logger.Info("[联机] 组件状态：" & LinkDiagnostics.ComponentState())
            Dim Code As String = LinkDiagnostics.NewInviteCode()
            Logger.Info("[联机] 邀请码生成：" & Code)
            Logger.Info("[联机] 邀请码解析：" & LinkDiagnostics.DescribeInvite("来看看我的房间 " & Code))
            Logger.Info("[联机] 非法码解析：" & LinkDiagnostics.DescribeInvite("hello world"))
            Logger.Info("[联机] TOML 预览：" & vbCrLf & LinkDiagnostics.PreviewHostToml())
            Logger.Info("[联机] ===== 自检结束 =====")
        Catch ex As Exception
            Logger.Error(ex, "[联机] 自检失败")
        End Try
    End Sub

End Module
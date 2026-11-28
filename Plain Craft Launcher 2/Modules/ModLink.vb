Imports System.IO
Imports Limlin.Link

''' <summary>
''' 启动器与联机库（Limlin.Link）之间的桥接层。
''' </summary>
Public Module ModLink

    ''' <summary>当前联机会话（Limlin.Link.LinkSession），未联机时为 Nothing。</summary>
    Public Session As Object = Nothing

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
            If Session Is Nothing Then Return False
            Try
                Return CBool(Session.GetType().GetProperty("IsRunning").GetValue(Session))
            Catch ex As Exception
                Return False
            End Try
        End Get
    End Property

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

    ''' <summary>开发自检：由启动目录下的 link-selftest.on 标记触发。</summary>
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
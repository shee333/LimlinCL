Imports System.Windows.Threading

''' <summary>联机页：创建房间 / 加入房间。</summary>
Public Class PageLinkMain

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum

    ''' <summary>当前联机状态（供 FormMain 的剪贴板自动加入逻辑判断）。</summary>
    Public Shared LinkState As LinkStates = LinkStates.Waiting

    Private IsFirstLoaded As Boolean = False
    Private IsBusy As Boolean = False
    Private IsHostMode As Boolean = False
    Private InviteCode As String = ""

    ' ========== 对外静态接口（兼容 FormMain 的自动加入逻辑） ==========

    ''' <summary>校验邀请码格式；合法返回 Nothing，否则返回错误原因。</summary>
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If String.IsNullOrWhiteSpace(Code) Then Return "邀请码为空"
        Try
            If ModLink.DescribeInvite(Code) = "无效邀请码" Then Return "不是有效的邀请码"
        Catch ex As Exception
            Return ex.Message
        End Try
        Return Nothing
    End Function

    ''' <summary>只切换到联机页。</summary>
    Public Shared Sub Join()
        Try
            If FrmMain Is Nothing Then Return
            FrmMain.PageChange(FormMain.PageType.Link)
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 打开联机页失败")
        End Try
    End Sub

    ''' <summary>切换到联机页并加入指定房间。</summary>
    Public Shared Sub Join(Code As String)
        Try
            If FrmMain Is Nothing Then Return
            FrmMain.PageChange(FormMain.PageType.Link)
            If FrmLinkMain IsNot Nothing Then FrmLinkMain.JoinInternal(Code)
        Catch ex As Exception
            Logger.Warn(ex, "[联机] 自动加入失败")
        End Try
    End Sub

    ' ========== 生命周期 ==========

    Private Sub Init() Handles Me.Loaded
        If IsFirstLoaded Then Return
        IsFirstLoaded = True
        AddHandler ModLink.SessionProgress, AddressOf OnSessionProgress
        AddHandler ModLink.SessionInvite, AddressOf OnSessionInvite
        AddHandler ModLink.SessionFailed, AddressOf OnSessionFailed
        AddHandler ModLink.SessionEnded, AddressOf OnSessionEnded
        UpdateUi()
    End Sub

    Private Sub PageEntered() Handles Me.PageEnter
        UpdateUi()
    End Sub

    ' ========== 界面 ==========

    Private Sub UpdateUi()
        Dim Running As Boolean = ModLink.IsRunning
        BtnCreate.IsEnabled = Not IsBusy AndAlso Not Running
        BtnJoin.IsEnabled = Not IsBusy AndAlso Not Running
        BtnCancel.Visibility = If(IsBusy, Visibility.Visible, Visibility.Collapsed)
        BtnCancel.IsEnabled = IsBusy
        BtnCopyInvite.IsEnabled = InviteCode.Length > 0
        BtnStop.IsEnabled = Running
        If Not Running AndAlso Not IsBusy AndAlso LinkState = LinkStates.Waiting Then
            LabStatus.Text = "尚未开始联机"
            CardRoom.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub SetBusy(Busy As Boolean, Optional Status As String = Nothing)
        IsBusy = Busy
        If Status IsNot Nothing Then LabStatus.Text = Status
        UpdateUi()
    End Sub

    ' ========== 按钮 ==========

    Private Sub BtnCreate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCreate.Click
        If IsBusy OrElse ModLink.IsRunning Then Return
        IsHostMode = True
        LinkState = LinkStates.Loading
        SetBusy(True, "正在准备联机…")
        Try
            If Not ModLink.StartHost() Then
                LinkState = LinkStates.Waiting
                SetBusy(False, "无法启动联机，请稍后重试")
            End If
        Catch ex As Exception
            Logger.Error(ex, "[联机] 创建房间失败")
            LinkState = LinkStates.Failed
            SetBusy(False, "创建房间失败：" & ex.Message)
        End Try
    End Sub


    Private Sub BtnJoin_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnJoin.Click
        StartJoin(TxtInvite.Text)
    End Sub

    ''' <summary>从外部（剪贴板自动加入）或按钮进入加入流程。</summary>
    Public Sub JoinInternal(Code As String)
        TxtInvite.Text = Code
        StartJoin(Code)
    End Sub

    Private Sub StartJoin(Code As String)
        If IsBusy OrElse ModLink.IsRunning Then Return
        If String.IsNullOrWhiteSpace(Code) Then
            Hint("请先粘贴邀请码", HintType.Red)
            Return
        End If
        IsHostMode = False
        LinkState = LinkStates.Loading
        SetBusy(True, "正在加入房间…")
        Try
            If Not ModLink.JoinRoom(Code) Then
                LinkState = LinkStates.Waiting
                SetBusy(False, "无法启动联机，请稍后重试")
            End If
        Catch ex As Exception
            Logger.Error(ex, "[联机] 加入房间失败")
            LinkState = LinkStates.Failed
            SetBusy(False, "加入房间失败：" & ex.Message)
        End Try
    End Sub

    Private Sub BtnCopyInvite_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCopyInvite.Click
        If InviteCode.Length = 0 Then Return
        ClipboardSet(InviteCode, False)
        Hint("已复制邀请码", HintType.Green)
    End Sub

    Private Sub BtnCancel_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCancel.Click
        ModLink.StopSession()
        ResetUi("已取消联机")
    End Sub
    Private Sub BtnStop_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnStop.Click
        ModLink.StopSession()
        ResetUi("已离开房间")
    End Sub

    ' ========== 会话事件（ModLink 已在 UI 线程回调） ==========

    Private Sub OnSessionProgress(Text As String)
        LabStatus.Text = Text
    End Sub

    Private Sub OnSessionInvite(Code As String)
        InviteCode = Code
        LinkState = LinkStates.Finished
        IsBusy = False
        CardRoom.Visibility = Visibility.Visible
        LabRoomTitle.Text = "把下面的邀请码发给好友，好友在本页「加入房间」中输入即可："
        LabInvite.Text = Code
        LabRoomInfo.Text = "房间已创建，等待好友加入"
        BtnStop.Text = "关闭房间"
        SetBusy(False, "房间已创建")
    End Sub

    Private Sub OnSessionFailed(Message As String)
        LinkState = LinkStates.Failed
        IsBusy = False
        UpdateUi()
        LabStatus.Text = "联机失败：" & Message
        Hint("联机失败：" & Message, HintType.Red)
    End Sub

    Private Sub OnSessionEnded()
        ResetUi("已离开房间")
    End Sub

    Private Sub ResetUi(Status As String)
        InviteCode = ""
        IsHostMode = False
        LinkState = LinkStates.Waiting
        IsBusy = False
        CardRoom.Visibility = Visibility.Collapsed
        LabStatus.Text = Status
        UpdateUi()
    End Sub

    ' ========== 离开页面 ==========

    ''' <summary>尝试离开联机页；返回 True 表示否决离开。</summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If Not ModLink.IsRunning Then Return False
        If Not Silent Then
            Dim Text As String = If(IsHostMode, "你确定要关闭联机房间吗？" & vbCrLf & "所有玩家都需要重新输入邀请码才可加入游戏！", "你确定要离开联机房间吗？")
            If MyMsgBox(Text, "退出联机", "确定", "取消", IsWarn:=IsHostMode) = 2 Then Return True
        End If
        ModLink.StopSession()
        ResetUi("已离开房间")
        Return False
    End Function

End Class
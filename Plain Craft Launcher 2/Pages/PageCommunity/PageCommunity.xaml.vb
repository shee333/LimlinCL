Imports System.Threading

''' <summary>
''' 社区游戏页：展示社区服务器列表与实时状态。
''' </summary>
Public Class PageCommunity

    Private ReadOnly Cards As New List(Of CommunityServerCard)
    Private IsQuerying As Boolean = False
    Private IsFirstLoaded As Boolean = False

    Private Sub Init() Handles Me.Loaded
        If IsFirstLoaded Then Return
        IsFirstLoaded = True
        RefreshAll()
    End Sub

    Private Sub PageEntered() Handles Me.PageEnter
        If IsFirstLoaded AndAlso Not IsQuerying Then RefreshAll()
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefresh.Click
        RefreshAll()
    End Sub

    Private Sub BtnEditConfig_Click(sender As Object, e As EventArgs) Handles BtnEditConfig.Click
        ModCommunity.EnsureServersFile()
        OpenExplorer(ModCommunity.GetServersFilePath())
        Hint($"已打开服务器列表文件：{ModCommunity.GetServersFilePath()}", HintType.Blue)
    End Sub

    ''' <summary>重新加载列表并查询所有服务器状态。</summary>
    Private Sub RefreshAll()
        If IsQuerying Then Return
        IsQuerying = True
        BtnRefresh.IsEnabled = False
        LabRefreshTime.Text = "正在查询服务器状态……"
        PanServers.Children.Clear()
        Cards.Clear()

        Dim Servers As List(Of ModCommunity.CommunityServer)
        Try
            Servers = ModCommunity.LoadServers().Where(Function(s) s.Enabled).ToList()
        Catch ex As Exception
            Logger.Error(ex, "读取社区服务器列表失败")
            Servers = New List(Of ModCommunity.CommunityServer)
        End Try

        If Servers.Count = 0 Then
            PanServers.Children.Add(BuildEmptyCard("尚未配置社区服务器", "请点击「编辑服务器列表」，在 servers.json 中添加服务器。"))
            LabRefreshTime.Text = "尚未配置服务器"
            BtnRefresh.IsEnabled = True
            IsQuerying = False
            Return
        End If

        For Each Server In Servers
            Dim Card As New CommunityServerCard(Server)
            Cards.Add(Card)
            PanServers.Children.Add(Card.Root)
        Next

        RunInNewThread(
            Sub()
                Dim Results As Dictionary(Of String, ModCommunity.CommunityServerStatus)
                Try
                    Results = ModCommunity.QueryServers(Servers)
                Catch ex As Exception
                    Logger.Error(ex, "查询社区服务器状态失败")
                    Results = New Dictionary(Of String, ModCommunity.CommunityServerStatus)
                End Try
                RunInUi(
                    Sub()
                        For Each Card In Cards
                            Dim Status As ModCommunity.CommunityServerStatus = Nothing
                            If Results.TryGetValue(Card.Server.Id, Status) Then Card.ApplyStatus(Status)
                        Next
                        LabRefreshTime.Text = "最后更新：" & Date.Now.ToString("HH:mm:ss")
                        BtnRefresh.IsEnabled = True
                        IsQuerying = False
                    End Sub)
            End Sub, "社区游戏状态查询", ThreadPriority.BelowNormal)
    End Sub

    Private Function BuildEmptyCard(Title As String, Text As String) As MyCard
        Dim Card As New MyCard With {.Title = Title, .Margin = New Thickness(0, 0, 0, 15)}
        Dim Panel As New TextBlock With {
            .Margin = New Thickness(25, 40, 23, 15),
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13,
            .Text = Text
        }
        Card.Children.Add(Panel)
        Return Card
    End Function

End Class

''' <summary>
''' 单个服务器卡片：负责构建控件与刷新状态显示。
''' </summary>
Public Class CommunityServerCard

    Public ReadOnly Root As MyCard
    Public ReadOnly Server As ModCommunity.CommunityServer

    Private ReadOnly LabStatus As TextBlock
    Private ReadOnly LabMotd As TextBlock
    Private ReadOnly LabPlayers As TextBlock
    Private ReadOnly LabNotes As TextBlock

    Private Shared ReadOnly BrushOnline As New SolidColorBrush(Color.FromRgb(&H4C, &HAF, &H50))
    Private Shared ReadOnly BrushOffline As New SolidColorBrush(Color.FromRgb(&HE5, &H39, &H35))
    Private Shared ReadOnly BrushPending As New SolidColorBrush(Color.FromRgb(&H9E, &H9E, &H9E))

    Public Sub New(Server As ModCommunity.CommunityServer)
        Me.Server = Server
        Root = New MyCard With {.Title = Server.Name, .Margin = New Thickness(0, 0, 0, 15)}
        Dim Panel As New StackPanel With {.Margin = New Thickness(25, 40, 23, 15)}

        '状态行
        LabStatus = New TextBlock With {
            .FontSize = 12.5,
            .FontWeight = FontWeights.Bold,
            .Text = "● 正在查询……",
            .Foreground = BrushPending
        }
        Panel.Children.Add(LabStatus)

        '地址与分组
        Dim AddressText As String = Server.DisplayAddress
        If Not String.IsNullOrWhiteSpace(Server.Group) Then AddressText &= $"（{Server.Group}）"
        Dim LabAddress As New TextBlock With {
            .Text = AddressText,
            .FontSize = 12.5,
            .Opacity = 0.7,
            .Margin = New Thickness(0, 5, 0, 0)
        }
        Panel.Children.Add(LabAddress)

        'MOTD
        LabMotd = New TextBlock With {
            .FontSize = 13.5,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 8, 0, 0),
            .LineHeight = 20,
            .Text = "正在获取服务器信息……"
        }
        Panel.Children.Add(LabMotd)

        '玩家
        LabPlayers = New TextBlock With {
            .FontSize = 12.5,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 8, 0, 0),
            .Opacity = 0.9,
            .Text = "在线玩家：查询中……"
        }
        Panel.Children.Add(LabPlayers)

        '游玩注意
        If Not String.IsNullOrWhiteSpace(Server.Notes) Then
            LabNotes = New TextBlock With {
                .Text = Server.Notes,
                .TextWrapping = TextWrapping.Wrap,
                .FontSize = 12.5,
                .Margin = New Thickness(0, 8, 0, 0)
            }
            Panel.Children.Add(LabNotes)
        End If

        '按钮
        Dim Buttons As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 12, 0, 0)}
        Dim BtnJoin As New MyButton With {
            .Text = "一键加入（即将开放）",
            .Height = 35,
            .Padding = New Thickness(20, 0, 20, 0),
            .ColorType = MyButton.ColorState.Highlight,
            .IsEnabled = False
        }
        ToolTipService.SetShowOnDisabled(BtnJoin, True)
        BtnJoin.ToolTip = "即将开放：将自动准备所需客户端并直接进入该服务器"
        Dim BtnCopy As New MyTextButton With {
            .Text = "复制地址",
            .Margin = New Thickness(14, 0, 0, 0),
            .VerticalAlignment = VerticalAlignment.Center
        }
        AddHandler BtnCopy.Click, Sub(sender, e) ClipboardSet(Server.DisplayAddress)
        Buttons.Children.Add(BtnJoin)
        Buttons.Children.Add(BtnCopy)
        Panel.Children.Add(Buttons)

        Root.Children.Add(Panel)
    End Sub

    ''' <summary>把查询结果刷新到界面上。</summary>
    Public Sub ApplyStatus(Status As ModCommunity.CommunityServerStatus)
        If Status Is Nothing Then Return
        If Status.Ok Then
            LabStatus.Text = $"● 在线    {Status.Online} / {Status.Max} 名玩家    延迟 {Status.Latency} ms" &
                             If(String.IsNullOrWhiteSpace(Status.VersionName), "", $"    {Status.VersionName}")
            LabStatus.Foreground = BrushOnline
            'MOTD
            If Status.Motd IsNot Nothing AndAlso Status.Motd.Count > 0 Then
                RenderMotd(Status.Motd)
            Else
                LabMotd.Text = "（服务器未返回 MOTD）"
            End If
            '玩家列表
            If Not Server.ShowPlayers Then
                LabPlayers.Text = "在线玩家：已隐藏"
            ElseIf Status.Players.Count > 0 Then
                LabPlayers.Text = "在线玩家：" & String.Join("、", Status.Players)
            ElseIf Status.Online = 0 Then
                LabPlayers.Text = "在线玩家：无人在线"
            Else
                LabPlayers.Text = "在线玩家：服务器未公开名单"
            End If
        Else
            LabStatus.Text = $"● 离线    {Status.Message}"
            LabStatus.Foreground = BrushOffline
            LabMotd.Inlines.Clear()
            If Not String.IsNullOrWhiteSpace(Server.MotdOverride) Then
                RenderMotd(ModCommunity.ParseMotdText(Server.MotdOverride))
            Else
                LabMotd.Text = "暂时无法获取服务器信息，请稍后重试。"
            End If
            LabPlayers.Text = "在线玩家：—"
        End If
    End Sub

    ''' <summary>按行/样式渲染 MOTD。</summary>
    Private Sub RenderMotd(Lines As List(Of List(Of ModCommunity.MotdPart)))
        LabMotd.Inlines.Clear()
        For LineIndex As Integer = 0 To Lines.Count - 1
            If LineIndex > 0 Then LabMotd.Inlines.Add(New LineBreak())
            For Each Part As ModCommunity.MotdPart In Lines(LineIndex)
                Dim TextRun As New Run(Part.Text)
                If Part.ColorHex IsNot Nothing Then TextRun.Foreground = New SolidColorBrush(ParseHexColor(Part.ColorHex))
                If Part.Bold Then TextRun.FontWeight = FontWeights.Bold
                If Part.Italic Then TextRun.FontStyle = FontStyles.Italic
                If Part.Underline AndAlso Part.Strike Then
                    TextRun.TextDecorations = New TextDecorationCollection From {TextDecorations.Underline(0), TextDecorations.Strikethrough(0)}
                ElseIf Part.Underline Then
                    TextRun.TextDecorations = TextDecorations.Underline
                ElseIf Part.Strike Then
                    TextRun.TextDecorations = TextDecorations.Strikethrough
                End If
                LabMotd.Inlines.Add(TextRun)
            Next
        Next
    End Sub

    Private Function ParseHexColor(Hex As String) As Color
        Try
            If Hex.Length <> 6 Then Return Colors.Gray
            Return Color.FromRgb(Convert.ToByte(Hex.Substring(0, 2), 16),
                                 Convert.ToByte(Hex.Substring(2, 2), 16),
                                 Convert.ToByte(Hex.Substring(4, 2), 16))
        Catch
            Return Colors.Gray
        End Try
    End Function

End Class

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace LumaIPTV;
public partial class MainWindow : Window
{
    XtreamClient? api;
    LibVLC? vlc;
    LibVLCSharp.Shared.MediaPlayer? player;
    readonly SemaphoreSlim playbackGate = new(1, 1);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    CancellationTokenSource libraryCts = new(), guideCts = new();
    List<Channel> channels = new();
    List<Category> categories = new();
    HashSet<string> favorites = new();
    Channel? selected;
    string kind = "live", accountKey = "demo";
    bool demo, favoritesOnly, isEpisodes, changing, fullScreen, closing;
    int playGeneration;
    WindowState previousState;

    public MainWindow()
    {
        InitializeComponent();
        var saved = SettingsStore.Load();
        if (saved != null) { ServerBox.Text = saved.Server; UsernameBox.Text = saved.Username; PasswordInput.Password = saved.Password; RememberBox.IsChecked = true; }
        timer.Tick += (_, _) => UpdatePlayback();
        timer.Start();
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            closing = true;
            libraryCts.Cancel(); guideCts.Cancel(); timer.Stop(); ++playGeneration;
            await playbackGate.WaitAsync();
            try
            {
                Video.MediaPlayer = null;
                if (player != null) await Task.Run(() => { player.Stop(); player.Dispose(); });
                vlc?.Dispose(); api?.Dispose();
            }
            finally { playbackGate.Release(); Close(); }
        };
    }
    static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    void Status(string text, bool error = false) { StatusText.Text = text; StatusText.Foreground = Color(error ? "#FFABAB" : "#91A6C1"); }
    async void ConnectClick(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = DemoButton.IsEnabled = false;
        LoginError.Text = ""; ConnectButton.Content = "Connecting…";
        XtreamClient? next = null;
        try
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrEmpty(PasswordInput.Password)) throw new ArgumentException("Enter your username and password.");
            var login = new Login(XtreamClient.NormalizeServer(ServerBox.Text), UsernameBox.Text.Trim(), PasswordInput.Password);
            next = new XtreamClient(login);
            var expiration = await next.Authenticate(libraryCts.Token);
            api?.Dispose(); api = next; next = null;
            demo = false; accountKey = SettingsStore.AccountKey(login); favorites = SettingsStore.Favorites(accountKey);
            string? saveWarning = null;
            try { if (RememberBox.IsChecked == true) SettingsStore.Save(login); else SettingsStore.Forget(); }
            catch { saveWarning = "Connected, but Windows could not save your account preference."; }
            PasswordInput.Clear();
            AccountText.Text = login.Username;
            AccountText.ToolTip = long.TryParse(expiration, out var stamp) && stamp > 0 ? ExpirationText(stamp) : "Connected account";
            ShowApp();
            await LoadLibrary("live");
            if (saveWarning != null) Status(saveWarning, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LoginError.Text = Friendly(ex); }
        finally { next?.Dispose(); ConnectButton.IsEnabled = DemoButton.IsEnabled = true; ConnectButton.Content = "Connect account  →"; }
    }
    static string ExpirationText(long stamp) { try { return "Expires " + DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("d"); } catch { return "Connected account"; } }
    static string Friendly(Exception ex) => ex is InvalidOperationException or ArgumentException ? ex.Message : "Something went wrong. Please try again.";
    void DemoClick(object sender, RoutedEventArgs e) => OpenDemo();
    public void OpenDemo()
    {
        demo = true; accountKey = "demo"; favorites = new(); AccountText.Text = "Demo account";
        ShowApp(); _ = LoadLibrary("live");
    }
    void ShowApp()
    {
        LoginScreen.Visibility = Visibility.Collapsed; AppScreen.Visibility = Visibility.Visible;
        DemoBadge.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        favoritesOnly = false; SearchBox.Text = "";
        Status(demo ? "Demo mode · Sample titles only. Connect your account to watch your streams." : "Connected to your provider.");
        AdaptLayout();
    }
    async void NavClick(object sender, RoutedEventArgs e) { favoritesOnly = false; await LoadLibrary((string)((Button)sender).Tag); }
    async void RefreshClick(object sender, RoutedEventArgs e) => await LoadLibrary(kind);
    async Task LoadLibrary(string target)
    {
        libraryCts.Cancel(); libraryCts = new(); var ct = libraryCts.Token;
        guideCts.Cancel(); kind = target; isEpisodes = false;
        BackButton.Visibility = Visibility.Collapsed;
        PageTitle.Text = target == "live" ? "Live TV" : target == "movie" ? "Movies" : "Series";
        SetNav(); channels = new(); CategoryBox.ItemsSource = null; Filter();
        Status("Loading your " + PageTitle.Text.ToLowerInvariant() + "…");
        try
        {
            if (demo) LoadDemoLibrary(target);
            else
            {
                if (api == null) return;
                var result = await api.Library(target, ct);
                ct.ThrowIfCancellationRequested();
                (categories, channels) = result;
            }
            changing = true;
            CategoryBox.ItemsSource = new[] { new Category("", "All categories") }.Concat(categories).ToList();
            CategoryBox.SelectedIndex = 0; SearchBox.Text = ""; changing = false;
            Filter();
            Status(demo ? "Demo mode · Sample titles only. Sign out to connect your account." : $"{channels.Count:N0} titles loaded · Select a title to watch.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) { Status(Friendly(ex), true); EmptyLibrary.Text = "Unable to load this library.\nUse refresh to try again."; EmptyLibrary.Visibility = Visibility.Visible; } }
    }
    void LoadDemoLibrary(string target)
    {
        categories = new() { new("1", "Entertainment"), new("2", "Discover"), new("3", "Sport & culture") };
        string[] names = target switch
        {
            "live" => new[] { "Luma One", "World News", "The Cinema Club", "Earth & Beyond", "Studio Sessions", "Matchday Live", "Design Channel", "Slow Living", "After Hours", "The Food Edit", "Coast to Coast", "History Now" },
            "movie" => new[] { "A Place in the Sun", "The Last Light", "Ocean of Stars", "Northbound", "City of Tomorrow", "The Long Weekend", "Wild at Heart", "Another Summer" },
            _ => new[] { "The Quiet City", "Beyond the Horizon", "Common Ground", "Somewhere New", "Field Notes", "Open Roads" }
        };
        channels = names.Select((name, i) => new Channel((i + 1).ToString(),name,((i % 3) + 1).ToString(),target)).ToList();
    }
    void SetNav()
    {
        foreach (var button in new[] { LiveNav, MoviesNav, SeriesNav })
        {
            var active = (string)button.Tag == kind;
            button.Background = Color(active ? "#234038" : "#101827"); button.Foreground = Color(active ? "#67E8CB" : "#B4C3D9");
        }
        FavoritesNav.Foreground = Color(favoritesOnly ? "#67E8CB" : "#B4C3D9");
    }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (SearchHint != null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(); }
    void CategoryChanged(object sender, SelectionChangedEventArgs e) => Filter();
    void Filter()
    {
        if (changing || ChannelList == null) return;
        var query = SearchBox.Text.Trim(); var category = CategoryBox.SelectedItem as Category;
        var filtered = channels.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && (category == null || category.Id.Length == 0 || c.CategoryId == category.Id) && (!favoritesOnly || favorites.Contains(c.Key))).ToList();
        changing = true; ChannelList.ItemsSource = filtered; ChannelList.SelectedItem = selected; changing = false;
        ResultCount.Text = $"{filtered.Count:N0} {(isEpisodes ? "EPISODES" : kind == "live" ? "CHANNELS" : "TITLES")}" + (favoritesOnly ? " · FAVORITES" : "");
        EmptyLibrary.Text = favoritesOnly ? "No favorites in this view yet.\nUse the heart beside a title to save it." : "No matches.\nTry another search or category.";
        EmptyLibrary.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    async void ChannelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (changing || ChannelList.SelectedItem is not Channel channel) return;
        selected = channel; NowTitle.Text = channel.Name; NowSubtitle.Text = channel.Subtitle;
        FavoriteButton.IsEnabled = true; UpdateFavorite();
        guideCts.Cancel(); guideCts = new(); var ct = guideCts.Token;
        if (channel.Kind == "series") { await ShowEpisodes(channel); return; }
        await PlaySelected();
        if (ct.IsCancellationRequested) return;
        if (channel.Kind != "live") { GuideText.Text = "On-demand playback · Use the timeline to seek once the video has loaded."; return; }
        if (demo) { GuideText.Text = "18:00   The evening edit\n19:00   A world worth discovering\n20:30   After the headlines\n\nSample schedule · Not a live broadcast"; return; }
        GuideText.Text = "Loading program guide…";
        try
        {
            var items = await api!.Guide(channel.Id, ct);
            if (!ct.IsCancellationRequested) GuideText.Text = items.Count == 0 ? "Your provider has no guide information for this channel." : string.Join("\n\n",items.Select(p => p.Time + "   " + p.Title + (p.Description.Length > 0 ? "\n" + p.Description : "")));
        }
        catch (OperationCanceledException) { }
        catch { if (!ct.IsCancellationRequested) GuideText.Text = "Program guide is unavailable. You can still watch the channel."; }
    }
    async Task ShowEpisodes(Channel series)
    {
        libraryCts.Cancel(); libraryCts = new(); var ct = libraryCts.Token;
        Status("Loading episodes…");
        try
        {
            var episodes = demo ? Enumerable.Range(1,8).Select(i => new Channel($"{series.Id}-{i}",$"{series.Name} · Episode {i}","1","episode","mp4",$"Season 1 · Episode {i}")).ToList() : await api!.Episodes(series.Id, ct);
            ct.ThrowIfCancellationRequested();
            channels = episodes; isEpisodes = true; favoritesOnly = false; SetNav();
            PageTitle.Text = series.Name; BackButton.Visibility = Visibility.Visible;
            changing = true; SearchBox.Text = "";
            CategoryBox.ItemsSource = new[] { new Category("", "All seasons") }.Concat(episodes.Select(c => c.CategoryId).Distinct().Select(id => new Category(id,"Season " + id))).ToList();
            CategoryBox.SelectedIndex = 0; changing = false; Filter();
            GuideText.Text = "Choose an episode from the list to start watching."; Status($"{episodes.Count} episodes available.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Status(Friendly(ex), true); }
    }
    async void BackClick(object sender, RoutedEventArgs e) => await LoadLibrary("series");
    void FavoritesClick(object sender, RoutedEventArgs e) { favoritesOnly = !favoritesOnly; SetNav(); Filter(); }
    void FavoriteClick(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        if (!favorites.Add(selected.Key)) favorites.Remove(selected.Key);
        try { if (!demo) SettingsStore.SaveFavorites(accountKey, favorites); }
        catch { Status("Favorite changed for this session, but could not be saved to disk.", true); }
        UpdateFavorite(); if (favoritesOnly) Filter();
    }
    void UpdateFavorite() => FavoriteButton.Content = selected != null && favorites.Contains(selected.Key) ? "♥" : "♡";
    void EnsurePlayer()
    {
        if (player != null) return;
        Core.Initialize();
        vlc = new LibVLC("--quiet", "--no-video-title-show", "--network-caching=1500");
        player = new LibVLCSharp.Shared.MediaPlayer(vlc) { Volume = (int)VolumeSlider.Value, EnableMouseInput = false, EnableKeyInput = false };
        Video.MediaPlayer = player;
    }
    async Task PlaySelected()
    {
        var channel = selected;
        if (channel == null || channel.Kind == "series" || closing) return;
        if (demo)
        {
            PlaceholderTitle.Text = channel.Name; PlaceholderDetail.Text = "This is a demo title. Connect your Xtream account to start watching.";
            Status("Demo preview · No stream is associated with this sample title."); return;
        }
        var generation = ++playGeneration;
        var format = FormatBox.SelectedIndex == 1 ? "m3u8" : "ts";
        await playbackGate.WaitAsync();
        try
        {
            if (generation != playGeneration || closing) return;
            EnsurePlayer();
            await Task.Run(() => player!.Stop());
            if (generation != playGeneration || closing) return;
            Video.Visibility = Visibility.Visible; PlayerPlaceholder.Visibility = Visibility.Collapsed;
            var url = api!.StreamUrl(channel, format);
            using var media = new Media(vlc!,url,FromType.FromLocation);
            var started = player!.Play(media);
            if (!started) throw new InvalidOperationException("The stream could not be opened. Try the other live format or another title.");
            PlayPauseButton.Content = "Ⅱ"; Status("Opening " + channel.Name + "…");
        }
        catch { ShowPlaybackError(); }
        finally { playbackGate.Release(); }
    }
    void ShowPlaybackError()
    {
        Video.Visibility = Visibility.Collapsed; PlayerPlaceholder.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = "This stream couldn't be opened";
        PlaceholderDetail.Text = "Try another title, or switch MPEG-TS / HLS and press play. Your provider may also limit simultaneous connections.";
        Status("Playback failed. Check the stream format and provider availability.", true);
        PlayPauseButton.Content = "▶";
    }
    async void PlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (player != null && player.State is VLCState.Playing or VLCState.Paused) player.Pause();
        else if (selected != null) await PlaySelected();
        else Status("Choose a title from the library first.");
    }
    async void StopClick(object sender, RoutedEventArgs e) => await StopPlayback();
    async Task StopPlayback()
    {
        ++playGeneration;
        await playbackGate.WaitAsync();
        try { if (player != null) await Task.Run(() => player.Stop()); }
        finally { playbackGate.Release(); }
        Video.Visibility = Visibility.Collapsed; PlayerPlaceholder.Visibility = Visibility.Visible;
        PlaceholderTitle.Text = "Ready when you are"; PlaceholderDetail.Text = "Choose a title or press play to continue.";
        PlayPauseButton.Content = "▶"; SeekSlider.Value = 0; TimeText.Text = "STOPPED"; Status("Playback stopped.");
    }
    void UpdatePlayback()
    {
        if (player == null || closing || playbackGate.CurrentCount == 0) return;
        var state = player.State;
        if (state == VLCState.Error && Video.Visibility == Visibility.Visible) { ShowPlaybackError(); return; }
        if (state is VLCState.Playing or VLCState.Paused)
        {
            PlayPauseButton.Content = state == VLCState.Playing ? "Ⅱ" : "▶";
            SeekSlider.IsEnabled = player.IsSeekable;
            if (!SeekSlider.IsMouseCaptureWithin && !SeekSlider.IsKeyboardFocusWithin) SeekSlider.Value = Math.Max(0,player.Position * 1000);
            TimeText.Text = player.Length > 0 ? Duration(player.Time) + " / " + Duration(player.Length) : "LIVE";
            if (StatusText.Text.StartsWith("Opening ")) Status("Playing · " + (selected?.Name ?? ""));
        }
        else
        {
            SeekSlider.IsEnabled = false;
            if (state is VLCState.Opening or VLCState.Buffering) TimeText.Text = "BUFFERING";
            if (state == VLCState.Ended) { PlayPauseButton.Content = "▶"; TimeText.Text = "ENDED"; }
        }
    }
    static string Duration(long millis) => TimeSpan.FromMilliseconds(Math.Max(0,millis)).ToString(millis >= 3600000 ? @"h\:mm\:ss" : @"mm\:ss");
    void SeekReleased(object sender, MouseButtonEventArgs e) => Seek();
    void SeekKeyUp(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) Seek(); }
    void Seek() { if (player?.IsSeekable == true) player.Position = (float)(SeekSlider.Value / 1000); }
    void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player != null) player.Volume = (int)e.NewValue; }
    void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void ToggleFullscreen()
    {
        fullScreen = !fullScreen;
        if (fullScreen)
        {
            previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized;
        }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousState; }
        foreach (var element in new FrameworkElement[] { Sidebar, Header, Filters, LibraryPanel, InfoPanel, StatusText }) element.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = new GridLength(fullScreen ? 0 : 186); ListColumn.Width = new GridLength(fullScreen ? 0 : 330); ContentGap.Width = new GridLength(fullScreen ? 0 : 20);
        InfoRow.Height = new GridLength(fullScreen ? 0 : 210); MainArea.Margin = new Thickness(fullScreen ? 0 : 28);
        if (!fullScreen) AdaptLayout();
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (AppScreen.Visibility != Visibility.Visible) return;
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && fullScreen) { ToggleFullscreen(); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not Button && Keyboard.FocusedElement is not ComboBox && Keyboard.FocusedElement is not Slider) { PlayPauseClick(this,new RoutedEventArgs()); e.Handled = true; }
    }
    void WindowResized(object sender, SizeChangedEventArgs e) => AdaptLayout();
    void AdaptLayout()
    {
        if (Sidebar == null || fullScreen) return;
        var compact = ActualWidth < 1120;
        SideColumn.Width = new GridLength(compact ? 76 : 186);
        ListColumn.Width = new GridLength(compact ? 260 : 330);
        ContentGap.Width = new GridLength(compact ? 14 : 20);
        Sidebar.Padding = new Thickness(compact ? 8 : 18,26,compact ? 8 : 18,22);
        BrandText.Visibility = LibraryLabel.Visibility = AccountBadge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LiveNav.Content = compact ? "◉" : "◉   Live TV"; MoviesNav.Content = compact ? "▣" : "▣   Movies"; SeriesNav.Content = compact ? "▤" : "▤   Series"; FavoritesNav.Content = compact ? "♡" : "♡   Favorites"; SignOutButton.Content = compact ? "↪" : "↪   Sign out";
        MainArea.Margin = new Thickness(compact ? 18 : 28,24,compact ? 18 : 28,18);
        InfoRow.Height = new GridLength(ActualHeight < 720 ? 150 : 210);
        LoginScreen.Margin = new Thickness(compact ? 24 : 44);
        WelcomePanel.Margin = new Thickness(0,0,compact ? 20 : 48,0);
        WelcomePanel.Visibility = ActualWidth < 960 ? Visibility.Collapsed : Visibility.Visible;
        LoginScreen.ColumnDefinitions[0].Width = ActualWidth < 960 ? new GridLength(0) : new GridLength(1,GridUnitType.Star);
        LoginScreen.ColumnDefinitions[1].Width = ActualWidth < 960 ? new GridLength(1,GridUnitType.Star) : new GridLength(440);
    }
    async void SignOutClick(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        libraryCts.Cancel(); guideCts.Cancel();
        await StopPlayback();
        if (fullScreen) ToggleFullscreen();
        api?.Dispose(); api = null;
        try { SettingsStore.Forget(); LoginError.Text = ""; }
        catch { LoginError.Text = "Signed out, but the saved account file could not be erased. Remove it from %LOCALAPPDATA%\\LumaIPTV."; }
        libraryCts = new(); selected = null; channels.Clear(); ChannelList.ItemsSource = null; PasswordInput.Clear(); RememberBox.IsChecked = false;
        NowTitle.Text = "Pick your perfect watch"; NowSubtitle.Text = "Your television, on your terms."; GuideText.Text = "Select a channel to see its program guide."; FavoriteButton.IsEnabled = false; FavoriteButton.Content = "♡";
        AppScreen.Visibility = Visibility.Collapsed; LoginScreen.Visibility = Visibility.Visible;
        SignOutButton.IsEnabled = true;
    }
    public void SaveScreenshot(string path)
    {
        UpdateLayout();
        var surface = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth,(int)surface.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
    public async Task<string> RunUiChecks(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks.Add("PASS: " + name); }
        ServerBox.Clear(); UsernameBox.Clear(); PasswordInput.Clear(); RememberBox.IsChecked = false;
        SaveScreenshot(Path.Combine(directory,"login.png"));
        OpenDemo();
        Check(ChannelList.Items.Count == 12,"Demo opens live library");
        SearchBox.Text = "World"; Check(ChannelList.Items.Count == 1,"Search narrows titles");
        SearchBox.Clear(); CategoryBox.SelectedIndex = 1; Check(ChannelList.Items.Count == 4,"Category filter"); CategoryBox.SelectedIndex = 0;
        ChannelList.SelectedIndex = 0; FavoriteClick(this,new RoutedEventArgs()); FavoritesClick(this,new RoutedEventArgs());
        Check(ChannelList.Items.Count == 1,"Favorites filter");
        FavoriteClick(this,new RoutedEventArgs()); Check(ChannelList.Items.Count == 0 && EmptyLibrary.Visibility == Visibility.Visible,"Favorites empty state");
        favoritesOnly = false; await LoadLibrary("movie"); Check(ChannelList.Items.Count == 8,"Movie navigation");
        await LoadLibrary("series"); ChannelList.SelectedIndex = 0;
        Check(isEpisodes && ChannelList.Items.Count == 8,"Series opens episodes");
        ChannelList.SelectedIndex = 0; Check(selected?.Kind == "episode", "Episode selection");
        await LoadLibrary("live"); ChannelList.SelectedIndex = 0;
        SaveScreenshot(Path.Combine(directory,"dashboard.png"));
        Width = 840; Height = 640; await Task.Delay(200); UpdateLayout();
        Check(SideColumn.Width.Value == 76 && PlayerColumn.ActualWidth > 350,"Compact layout preserves usable player");
        SaveScreenshot(Path.Combine(directory,"compact.png"));
        ToggleFullscreen(); Check(fullScreen && LibraryPanel.Visibility == Visibility.Collapsed,"Fullscreen isolates player");
        ToggleFullscreen(); Check(!fullScreen && LibraryPanel.Visibility == Visibility.Visible,"Exit fullscreen restores library");
        return string.Join(Environment.NewLine,checks) + $"\n\n{checks.Count} UI checks passed.\n";
    }
}

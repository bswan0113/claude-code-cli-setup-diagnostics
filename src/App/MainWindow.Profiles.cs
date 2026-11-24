using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private bool _profileSigningIn;
    private bool _profileSwitching;
    private bool _initialProfileLoginChecked;
    private int _profileTabGeneration;
    private static readonly Lazy<HashSet<string>> DefaultProfileNames = new(() =>
        new[] { "en", "ko", "zh-Hans", "ja" }.Select(language =>
        {
            var dictionary = new ResourceDictionary { Source =
                new Uri($"pack://application:,,,/App/Resources/Profiles.{language}.xaml") };
            return (string)dictionary["ProfileDefault"];
        }).Append("Default").ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static string ProfileText(string key) => UiLanguage.Text("Profile" + key);

    private static string ProfileLabel(LauncherProfile profile) =>
        profile.Id == "default" ? ProfileText("Default") : profile.Label;

    private static bool ProfileAccessError(Exception error) => error is System.IO.IOException or
        System.IO.InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or
        System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NotSupportedException or
        Win32Exception or WaitHandleCannotBeOpenedException or TimeoutException;

    private async void InitProfileTabs()
    {
        var generation = ++_profileTabGeneration;
        try
        {
            var profiles = await Task.Run(LauncherProfiles.List);
            if (_closing || generation != _profileTabGeneration)
                return;

            var current = LauncherProfiles.Current;
            var labels = profiles.ToLookup(ProfileLabel, StringComparer.OrdinalIgnoreCase);
            var buttons = new List<Button>(profiles.Count);
            foreach (var profile in profiles)
            {
                var active = profile.Id == current.Id;
                var name = ProfileLabel(profile);
                if (profile.Id != "default" && labels[name].Skip(1).Any())
                    name += " · " + profile.Id[..Math.Min(8, profile.Id.Length)];
                var button = new Button
                {
                    Tag = profile, Style = (Style)FindResource(active ? "PrimaryAction" : "Action"),
                    Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis },
                    ToolTip = name + Environment.NewLine + profile.ConfigRoot,
                    Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0),
                    MinHeight = 26, MaxWidth = 195
                };
                AutomationProperties.SetName(button, name);
                AutomationProperties.SetAutomationId(button, "AccountTab_" + profile.Id);
                if (active)
                {
                    AutomationProperties.SetItemStatus(button, ProfileText("Current"));
                    button.Loaded += (_, _) => button.BringIntoView();
                }
                button.Click += SwitchProfile_Click;
                buttons.Add(button);
            }
            ProfileTabs.ItemsSource = buttons;
            if (!_initialProfileLoginChecked)
            {
                _initialProfileLoginChecked = true;
                if (LauncherProfiles.ConsumeLoginRequest())
                {
                    await SignInFromTabAsync();
                    return;
                }
            }
            await SessionOwnerProfiles.RefreshAsync(profiles);
            if (!_closing && generation == _profileTabGeneration && _sessions is { } index)
            {
                var saved = index.Sessions.ToDictionary(session => session.Id);
                var rows = _shownFolders.SelectMany(folder => folder.Children.OfType<SessionTreeRow>())
                    .Concat(RecentSessions.Items.OfType<SessionTreeRow>());
                foreach (var row in rows)
                    if (saved.TryGetValue(row.Session.Id, out var session))
                        row.SetUsage(UsageText.Session(session.Usage, loading: session.Usage is null));
                foreach (var live in _liveRows)
                    if (live.SessionId is Guid id && saved.TryGetValue(id, out var liveSession))
                        live.SetUsage(UsageText.Session(liveSession.Usage, loading: liveSession.Usage is null));
            }
        }
        catch (Exception error) when (ProfileAccessError(error))
        {
            if (!_closing && generation == _profileTabGeneration)
                SetStatus("ProfileListFailed");
        }
    }

    private bool CanUseProfileAction()
    {
        if (_closing || _profileSwitching || _profileSigningIn)
            return false;
        if (_setup?.IsBusy == true || _changingSessions ||
            _operations.Any(operation => operation.Live is null || operation.Live.Activity == "Starting"))
        {
            SetStatus("ProfileBusy");
            return false;
        }
        return true;
    }

    private async void SwitchProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherProfile selected } || selected.Id == LauncherProfiles.Current.Id ||
            !CanUseProfileAction())
            return;
        await SwitchProfileAsync(selected);
    }

    private async Task SwitchProfileAsync(LauncherProfile profile, bool signIn = false)
    {
        _profileSwitching = true;
        LauncherSurface.IsEnabled = false;
        try
        {
            SetStatus("ProfileSwitching");
            var placement = CaptureSwitchPlacement();
            if (await Task.Run(() => LauncherProfiles.SwitchTo(profile, signIn, windowPlacement: placement)))
            {
                _profileSwitching = false;
                Close();
                return;
            }
            SetStatus("ProfileSwitchFailed");
        }
        catch (Exception error) when (ProfileAccessError(error))
        {
            SetStatus("ProfileSwitchFailed");
        }
        finally
        {
            _profileSwitching = false;
            if (!_closing)
            {
                LauncherSurface.IsEnabled = true;
                InitProfileTabs();
            }
        }
    }

    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseProfileAction() || AskProfileName() is not { } name || !CanUseProfileAction())
            return;
        LauncherProfile profile;
        try
        {
            _profileSwitching = true;
            LauncherSurface.IsEnabled = false;
            profile = await Task.Run(() => LauncherProfiles.Create(name));
        }
        catch (ArgumentException error) when (error.ParamName == "label")
        {
            SetStatus("ProfileDuplicateName");
            InitProfileTabs();
            return;
        }
        catch (Exception error) when (ProfileAccessError(error))
        {
            SetStatus("ProfileCreateFailed");
            return;
        }
        finally
        {
            _profileSwitching = false;
            LauncherSurface.IsEnabled = true;
        }
        if (!_closing && CanUseProfileAction())
            await SwitchProfileAsync(profile, signIn: true);
    }

    private async void ProfileLogin_Click(object sender, RoutedEventArgs e) => await SignInFromTabAsync();

    private async Task SignInFromTabAsync()
    {
        if (!CanUseProfileAction())
            return;
        _profileSigningIn = true;
        ProfileLoginButton.IsEnabled = false;
        try { await SignInToCurrentProfileAsync(); }
        finally
        {
            _profileSigningIn = false;
            ProfileLoginButton.IsEnabled = true;
            if (!_closing)
                InitProfileTabs();
        }
    }

    private string? AskProfileName()
    {
        var dialog = new Window
        {
            Title = ProfileText("Add"), Owner = this, ShowInTaskbar = false, Language = Language,
            FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(440, SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height, ResizeMode = ResizeMode.NoResize
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "Canvas");
        dialog.SetResourceReference(Control.ForegroundProperty, "Ink");
        dialog.SetResourceReference(Control.FontSizeProperty, "Font16");
        var content = new StackPanel { Margin = new Thickness(20) };
        var hint = new TextBlock { Text = ProfileText("AddHint"), Style = (Style)FindResource("BodyText") };
        content.Children.Add(hint);
        var input = new TextBox { Style = (Style)FindResource("Field") };
        var unavailableNames = ProfileTabs.Items.OfType<Button>().Select(button => button.Tag)
            .OfType<LauncherProfile>().Select(ProfileLabel).Concat(DefaultProfileNames.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var label = new Label { Content = ProfileText("Label"), Target = input, Padding = new Thickness(0, 12, 0, 4) };
        label.SetResourceReference(Control.ForegroundProperty, "Ink");
        AutomationProperties.SetLabeledBy(input, label);
        content.Children.Add(label);
        content.Children.Add(input);
        var validation = new TextBlock { Text = ProfileText("InvalidName"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        validation.SetResourceReference(TextBlock.ForegroundProperty, "IssueInk");
        content.Children.Add(validation);
        var commands = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = ProfileText("Cancel"), Style = (Style)FindResource("Action"), IsCancel = true };
        var create = new Button
        {
            Content = ProfileText("Create"), Style = (Style)FindResource("PrimaryAction"),
            IsDefault = true, IsEnabled = false, Margin = new Thickness(6, 0, 0, 0)
        };
        input.TextChanged += (_, _) =>
        {
            var name = input.Text.Trim();
            bool duplicate = unavailableNames.Contains(name);
            bool invalid = name.Length > 80 || name.Any(char.IsControl) || duplicate;
            create.IsEnabled = name.Length != 0 && !invalid;
            validation.Text = ProfileText(duplicate ? "DuplicateName" : "InvalidName");
            validation.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        };
        create.Click += (_, _) => dialog.DialogResult = true;
        commands.Children.Add(cancel);
        commands.Children.Add(create);
        content.Children.Add(commands);
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ContentRendered += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}

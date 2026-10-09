using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// A kind of model server to add or switch to.
/// </summary>
public sealed record ProviderOption(AIProviderKind Kind, string Label);

/// <summary>
/// One connection in Tools › AI Connections: the list row and the form beside it. Every change is
/// saved at once.
/// </summary>
public sealed partial class AIConnectionItemViewModel : ViewModelBase
{
    /// <summary>
    /// How long typing pauses before a typed key is saved: the key store is written once, not at
    /// every keystroke.
    /// </summary>
    internal static readonly TimeSpan KeySaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly AIConnectionsViewModel _owner;
    private readonly bool _ready;
    private DispatcherTimer? _keySave;

    internal AIConnectionItemViewModel(AIConnection connection, AIConnectionsViewModel owner)
    {
        _owner = owner;
        Id = connection.Id;
        SelectedProvider = owner.ProviderOptions.First(o => o.Kind == connection.Kind);
        Name = connection.Name;
        BaseUrl = connection.BaseUrl;
        Model = connection.Model;
        ApiKey = connection.ApiKey;
        HasStoredKey = connection.HasStoredKey;
        Temperature = (decimal)connection.Temperature;
        MaxTokens = connection.MaxTokens;
        AllowRemote = connection.AllowRemote;
        EmbeddingModel = connection.EmbeddingModel;
        _ready = true;
    }

    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloud), nameof(OffersEmbeddings), nameof(EmbeddingHint), nameof(IsAzure), nameof(RemoteWarning), nameof(KeyPlaceholder), nameof(KeyNote))]
    public partial ProviderOption SelectedProvider { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote), nameof(IsUrlInvalid), nameof(NeedsConsent))]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Model { get; set; }

    /// <summary>
    /// A key typed in this window. A saved key is not shown again: it stays in the key store.
    /// </summary>
    [ObservableProperty]
    public partial string? ApiKey { get; set; }

    /// <summary>
    /// The connection's key is in the operating system's key store (M19).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    public partial bool HasStoredKey { get; private set; }

    /// <summary>
    /// The model that turns questions into vectors (SDD §8.2); empty when none is chosen.
    /// </summary>
    [ObservableProperty]
    public partial string? EmbeddingModel { get; set; }

    /// <summary>
    /// 0 (always the same answer) to 2 (very varied).
    /// </summary>
    [ObservableProperty]
    public partial decimal? Temperature { get; set; }

    /// <summary>
    /// The longest answer in tokens; empty leaves it to the server.
    /// </summary>
    [ObservableProperty]
    public partial decimal? MaxTokens { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsConsent))]
    public partial bool AllowRemote { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>
    /// Models the server offered when it was last asked.
    /// </summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>
    /// "qwen2.5:7b · Ollama" for the list.
    /// </summary>
    public string Title => AIText.Describe(ToConnection());

    public bool IsUrlInvalid => AIProviders.ParseUrl(BaseUrl) is null;

    /// <summary>
    /// Records sent to this address leave this computer and its local network.
    /// </summary>
    public bool IsRemote => !AIProviders.IsLocal(BaseUrl);

    public bool NeedsConsent => IsRemote && !AllowRemote;

    /// <summary>
    /// A model service run by a company (OpenAI, Anthropic…) rather than a server of the user's.
    /// </summary>
    public bool IsCloud => AIProviders.IsCloud(SelectedProvider.Kind);

    public bool IsAzure => SelectedProvider.Kind == AIProviderKind.AzureOpenAI;

    /// <summary>
    /// Anthropic, Groq and DeepSeek cannot turn texts into vectors; the field is hidden for them.
    /// </summary>
    public bool OffersEmbeddings => AIProviders.OffersEmbeddings(SelectedProvider.Kind);

    /// <summary>
    /// A cloud service has its models ready; a server of the user's needs one downloaded first.
    /// </summary>
    public string EmbeddingHint => _owner.Localizer[IsCloud ? "AI_EmbeddingModelHintCloud" : "AI_EmbeddingModelHint"];

    /// <summary>
    /// What leaving the computer means: for a cloud service, its company's terms apply.
    /// </summary>
    public string RemoteWarning => IsCloud
        ? _owner.Localizer.Format("AI_CloudWarning", SelectedProvider.Label)
        : _owner.Localizer["AI_RemoteWarning"];

    public string KeyPlaceholder => HasStoredKey
        ? _owner.Localizer.Format("AI_ApiKeyStored", _owner.KeyStoreName)
        : _owner.Localizer[IsCloud ? "AI_ApiKeyRequired" : "AI_ApiKeyWatermark"];

    /// <summary>
    /// Where a typed key is kept, and for a cloud service the environment variable read otherwise.
    /// </summary>
    public string KeyNote
    {
        get
        {
            var note = _owner.KeyStoreName is { } store
                ? _owner.Localizer.Format("AI_ApiKeyKeptIn", store)
                : _owner.Localizer["AI_ApiKeyNoKeyStore"];
            return AIProviders.KeyVariable(SelectedProvider.Kind) is { } variable
                ? note + " " + _owner.Localizer.Format("AI_ApiKeyVariable", variable)
                : note;
        }
    }

    public bool IsKeyStoreMissing => _owner.KeyStoreName is null;

    /// <summary>
    /// Deletes the stored key; the connection then uses the environment variable, or none.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasStoredKey))]
    private void RemoveKey()
    {
        HasStoredKey = false;
        ApiKey = null;
        Save();
    }

    public AIConnection ToConnection() => new(
        Id,
        string.IsNullOrWhiteSpace(Name) ? SelectedProvider.Label : Name.Trim(),
        SelectedProvider.Kind,
        BaseUrl.Trim(),
        Model.Trim(),
        string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
        (double)Math.Clamp(Temperature ?? (decimal)AIConnection.DefaultTemperature, 0m, 2m),
        MaxTokens is > 0 ? (int)Math.Min(MaxTokens.Value, int.MaxValue) : null,
        AllowRemote && IsRemote,
        OffersEmbeddings && !string.IsNullOrWhiteSpace(EmbeddingModel) ? EmbeddingModel.Trim() : null,
        HasStoredKey);

    /// <summary>
    /// Takes in how the connection was saved: a typed key now lives in the key store.
    /// </summary>
    internal void Saved(AIConnection saved) => HasStoredKey = saved.HasStoredKey;

    partial void OnSelectedProviderChanged(ProviderOption oldValue, ProviderOption newValue)
    {
        if (!_ready)
        {
            return;
        }

        // Follow the server's defaults unless the user typed their own.
        if (string.IsNullOrWhiteSpace(BaseUrl) || BaseUrl == AIProviders.DefaultUrl(oldValue.Kind))
        {
            BaseUrl = AIProviders.DefaultUrl(newValue.Kind);
        }

        if (string.IsNullOrWhiteSpace(Name) || Name == oldValue.Label)
        {
            Name = newValue.Label;
        }

        // A key belongs to its service: it is never sent to another one.
        if (oldValue.Kind != newValue.Kind && (HasStoredKey || !string.IsNullOrWhiteSpace(ApiKey)))
        {
            HasStoredKey = false;
            ApiKey = null;
        }

        Save();
    }

    partial void OnNameChanged(string value) => Save();

    partial void OnBaseUrlChanged(string oldValue, string newValue)
    {
        // Consent is for one server: another host must be allowed again.
        if (AllowRemote && !string.Equals(Host(oldValue), Host(newValue), StringComparison.OrdinalIgnoreCase))
        {
            AllowRemote = false;
        }

        Save();
    }

    private static string? Host(string? url) => AIProviders.ParseUrl(url)?.Host;

    partial void OnModelChanged(string value) => Save();

    partial void OnApiKeyChanged(string? value)
    {
        if (!_ready)
        {
            return;
        }

        if (_keySave is null)
        {
            _keySave = new DispatcherTimer { Interval = KeySaveDelay };
            _keySave.Tick += (_, _) => Save();
        }

        _keySave.Stop();
        _keySave.Start();
    }

    partial void OnEmbeddingModelChanged(string? value) => Save();

    partial void OnTemperatureChanged(decimal? value) => Save();

    partial void OnMaxTokensChanged(decimal? value) => Save();

    partial void OnAllowRemoteChanged(bool value) => Save();

    /// <summary>
    /// Saves a key still being typed, e.g. when the window closes.
    /// </summary>
    internal void SavePendingKey()
    {
        if (_keySave?.IsEnabled == true)
        {
            Save();
        }
    }

    /// <summary>
    /// Forgets a key still being typed, for a connection being deleted.
    /// </summary>
    internal void DropPendingKey() => _keySave?.Stop();

    private void Save()
    {
        _keySave?.Stop();
        if (_ready)
        {
            Saved(_owner.Save(this));
        }
    }
}

/// <summary>
/// Tools › AI Connections (SDD §17): the local model servers that help write records. Changes
/// are saved at once; the default connection is the one the editor uses.
/// </summary>
public sealed partial class AIConnectionsViewModel : ViewModelBase
{
    private const string TestPrompt = "Reply with the single word OK.";

    private readonly IAIConnectionStore _store;
    private readonly IAIProviderFactory _providers;
    private readonly Localizer _localizer;
    private readonly Lazy<string?> _keyStoreName;
    private CancellationTokenSource? _work;

    public AIConnectionsViewModel(IAIConnectionStore store, IAIProviderFactory providers, Localizer localizer)
    {
        _store = store;
        _providers = providers;
        _localizer = localizer;
        _keyStoreName = new Lazy<string?>(() => store.KeyStore switch
        {
            SecretStoreKind.None => null,
            var kind => localizer[$"KeyStore_{kind}"],
        });
        ProviderOptions = [.. Enum.GetValues<AIProviderKind>().Select(kind => new ProviderOption(kind, AIText.Provider(localizer, kind)))];
        var defaultId = store.DefaultConnection?.Id;
        foreach (var connection in store.Connections)
        {
            Connections.Add(new AIConnectionItemViewModel(connection, this) { IsDefault = connection.Id == defaultId });
        }

        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        SelectedConnection = Connections.FirstOrDefault(c => c.IsDefault) ?? Connections.FirstOrDefault();
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<ProviderOption> ProviderOptions { get; }

    internal Localizer Localizer => _localizer;

    /// <summary>
    /// "Windows Credential Manager", or <c>null</c> when keys are kept in the settings file.
    /// </summary>
    internal string? KeyStoreName => _keyStoreName.Value;

    public ObservableCollection<AIConnectionItemViewModel> Connections { get; } = [];

    public bool IsEmpty => Connections.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(MakeDefaultCommand), nameof(TestCommand), nameof(RefreshModelsCommand))]
    public partial AIConnectionItemViewModel? SelectedConnection { get; set; }

    public bool HasSelection => SelectedConnection is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(RefreshModelsCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// The outcome of the last test, model refresh or change.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    [ObservableProperty]
    public partial bool IsResultError { get; private set; }

    public bool HasResult => Result is not null;

    /// <summary>
    /// The current test or model refresh; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Adds a connection to a server of the chosen kind at its default address, then asks it
    /// for its models.
    /// </summary>
    [RelayCommand]
    private Task AddAsync(ProviderOption? option)
    {
        option ??= ProviderOptions[0];
        var connection = new AIConnection(Guid.CreateVersion7(), option.Label, option.Kind, AIProviders.DefaultUrl(option.Kind), string.Empty);
        _store.Save(connection);
        var item = new AIConnectionItemViewModel(connection, this);
        Connections.Add(item);
        MarkDefault();
        SelectedConnection = item;

        // A cloud service is asked only once the user allowed it and gave a key.
        return item.IsRemote || item.IsUrlInvalid ? Task.CompletedTask : RefreshModelsAsync();
    }

    private bool HasSelectedConnection() => SelectedConnection is not null;

    private bool CanTalkToServer() => SelectedConnection is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    private void Delete()
    {
        if (SelectedConnection is not { } item)
        {
            return;
        }

        _work?.Cancel();
        item.DropPendingKey();
        _store.Remove(item.Id);
        var index = Connections.IndexOf(item);
        Connections.Remove(item);
        MarkDefault();
        SelectedConnection = Connections.Count == 0 ? null : Connections[Math.Min(index, Connections.Count - 1)];
        ShowResult(_localizer.Format("AI_Deleted", item.Title), isError: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    private void MakeDefault()
    {
        if (SelectedConnection is { } item)
        {
            _store.SetDefault(item.Id);
            MarkDefault();
        }
    }

    /// <summary>
    /// Asks the server for its models; an empty model is set to the first one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTalkToServer))]
    private Task RefreshModelsAsync() => Run(async (item, connection, token) =>
    {
        var models = await _providers.Create(connection).ListModelsAsync(token);
        Fill(item, models);
        ShowResult(models.Count == 0
            ? _localizer["AI_NoModels"]
            : _localizer.Format("AI_ModelsFound", models.Count, AIText.Provider(_localizer, connection.Kind)), isError: models.Count == 0);
    });

    /// <summary>
    /// Lists the models and has the chosen one answer a short question; the embedding model, if
    /// one is chosen, turns the question into a vector.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTalkToServer))]
    private Task TestAsync() => Run(async (item, connection, token) =>
    {
        var provider = _providers.Create(connection);
        Fill(item, await provider.ListModelsAsync(token));
        connection = item.ToConnection();
        if (string.IsNullOrWhiteSpace(connection.Model))
        {
            ShowResult(_localizer["AI_ChooseModel"], isError: true);
            return;
        }

        var watch = Stopwatch.StartNew();
        var answer = new System.Text.StringBuilder();
        var request = new AIChatRequest([new AIChatMessage(ChatRole.User, TestPrompt)], 0, 16);
        await foreach (var piece in ReasoningFilter.ApplyAsync(_providers.Create(connection).StreamChatAsync(request, token), token))
        {
            answer.Append(piece);
        }

        var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
        var result = _localizer.Format("AI_TestPassed", connection.Model, seconds, Shorten(answer.ToString()));
        if (connection.EmbeddingModel is { } embeddingModel)
        {
            var vector = (await _providers.Create(connection).EmbedAsync([TestPrompt], token))[0];
            result += Environment.NewLine + _localizer.Format("AI_EmbeddingTestPassed", embeddingModel, vector.Length);
        }

        ShowResult(result, isError: false);
    });

    [RelayCommand]
    private void Close()
    {
        _work?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves keys still being typed; called when the window closes, however it is closed.
    /// </summary>
    public void SavePendingKeys()
    {
        foreach (var connection in Connections)
        {
            connection.SavePendingKey();
        }
    }

    internal AIConnection Save(AIConnectionItemViewModel item)
    {
        var saved = _store.Save(item.ToConnection());
        Result = null;
        return saved;
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    private void Fill(AIConnectionItemViewModel item, IReadOnlyList<string> models)
    {
        item.Models.Clear();
        foreach (var model in models)
        {
            item.Models.Add(model);
        }

        if (string.IsNullOrWhiteSpace(item.Model) && models.Count > 0)
        {
            item.Model = models[0];
        }
    }

    private void MarkDefault()
    {
        var defaultId = _store.DefaultConnection?.Id;
        foreach (var item in Connections)
        {
            item.IsDefault = item.Id == defaultId;
        }
    }

    private void ShowResult(string text, bool isError)
    {
        Result = text;
        IsResultError = isError;
    }

    /// <summary>
    /// Runs one request against the selected connection, cancelling the previous one.
    /// </summary>
    private Task Run(Func<AIConnectionItemViewModel, AIConnection, CancellationToken, Task> work)
    {
        if (SelectedConnection is not { } item)
        {
            return Task.CompletedTask;
        }

        _work?.Cancel();
        var current = _work = new CancellationTokenSource();
        var token = current.Token;
        PendingWork = RunCore();
        return PendingWork;

        async Task RunCore()
        {
            IsBusy = true;
            Result = null;
            var connection = item.ToConnection();
            try
            {
                await work(item, connection, token);
            }
            catch (AIException ex)
            {
                ShowResult(AIText.Error(_localizer, ex, connection), isError: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by a newer request, or the window closed.
            }
            finally
            {
                if (_work == current)
                {
                    IsBusy = false;
                }
            }
        }
    }
}

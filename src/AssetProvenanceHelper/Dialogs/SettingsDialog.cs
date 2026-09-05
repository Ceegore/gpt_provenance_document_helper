using System.Drawing;
using System.Windows.Forms;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;
using AssetProvenanceHelper.Models;
using AssetProvenanceHelper.Services;
using AssetProvenanceHelper.Ui;

namespace AssetProvenanceHelper.Dialogs;

public sealed class SettingsDialog : Form
{
    public const string OpenAiApiKeySecretName = "openai_api_key";

    private readonly AppSettings _settings;
    private readonly ISecretStore _secretStore;
    private readonly OpenAiApiClient? _apiClient;

    private TabControl tabControl = null!;
    private TextBox txtApiKey = null!;
    private Button btnSaveKey = null!;
    private Button btnDeleteKey = null!;
    private Button btnTestConnection = null!;
    private Label lblConnectionStatus = null!;
    private ComboBox cmbModel = null!;
    private ComboBox cmbDirectQuality = null!;
    private ComboBox cmbBatchQuality = null!;
    private NumericUpDown numStartsPerMinute = null!;
    private NumericUpDown numMaxConcurrency = null!;
    private CheckBox chkNormalize = null!;
    private NumericUpDown numBatchPollSeconds = null!;
    private NumericUpDown numMaxBatchRequests = null!;
    private NumericUpDown numDirectRetryAttempts = null!;
    private Button btnOk = null!;
    private Button btnCancel = null!;

    public SettingsDialog(
        AppSettings settings,
        ISecretStore secretStore,
        OpenAiApiClient? apiClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _apiClient = apiClient;

        InitializeComponent();
        LoadSettingsIntoDialog();
    }

    private void InitializeComponent()
    {
        Text = "Settings";
        Size = new Size(520, 440);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.DefaultFont;

        tabControl = new TabControl
        {
            Dock = DockStyle.Top,
            Height = 330,
            Padding = new Point(12, 6)
        };

        var tabApi = new TabPage("API");
        var tabGeneration = new TabPage("Generation");
        var tabAdvanced = new TabPage("Batch & Advanced");

        BuildApiTab(tabApi);
        BuildGenerationTab(tabGeneration);
        BuildAdvancedTab(tabAdvanced);

        tabControl.TabPages.Add(tabApi);
        tabControl.TabPages.Add(tabGeneration);
        tabControl.TabPages.Add(tabAdvanced);

        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(12, 8, 12, 8)
        };

        btnOk = new Button
        {
            Name = "btnOk",
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(85, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(pnlBottom.Width - 185, 10),
            UseVisualStyleBackColor = true
        };
        btnOk.Click += (_, _) => ApplySettings();

        btnCancel = new Button
        {
            Name = "btnCancel",
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(85, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(pnlBottom.Width - 95, 10),
            UseVisualStyleBackColor = true
        };

        pnlBottom.Controls.Add(btnOk);
        pnlBottom.Controls.Add(btnCancel);

        Controls.Add(tabControl);
        Controls.Add(pnlBottom);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void BuildApiTab(TabPage tab)
    {
        tab.Padding = new Padding(16);

        var lblProvider = new Label { Text = "Provider:", Location = new Point(16, 20), AutoSize = true };
        var cmbProvider = new ComboBox
        {
            Location = new Point(140, 16),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbProvider.Items.Add("OpenAI");
        cmbProvider.SelectedIndex = 0;

        var lblKey = new Label { Text = "API Key:", Location = new Point(16, 56), AutoSize = true };
        txtApiKey = new TextBox
        {
            Name = "txtApiKey",
            Location = new Point(140, 52),
            Width = 320,
            UseSystemPasswordChar = true,
            PasswordChar = '*'
        };
        txtApiKey.TextChanged += (_, _) => UpdateApiKeyButtonStates();

        btnSaveKey = new Button
        {
            Name = "btnSaveKey",
            Text = "Save Key",
            Location = new Point(140, 84),
            Size = new Size(90, 28),
            UseVisualStyleBackColor = true
        };
        btnSaveKey.Click += (_, _) => HandleSaveKey();

        btnDeleteKey = new Button
        {
            Name = "btnDeleteApiKey",
            Text = "Delete Key",
            Location = new Point(240, 84),
            Size = new Size(90, 28),
            UseVisualStyleBackColor = true
        };
        btnDeleteKey.Click += (_, _) => HandleDeleteKey();

        var btnDeleteKeyAlias = new Button
        {
            Name = "btnDeleteKey",
            Visible = false
        };
        btnDeleteKeyAlias.Click += (_, _) => btnDeleteKey.PerformClick();

        btnTestConnection = new Button
        {
            Name = "btnTestConnection",
            Text = "Test Connection",
            Location = new Point(340, 84),
            Size = new Size(120, 28),
            UseVisualStyleBackColor = true
        };
        btnTestConnection.Click += async (_, _) => await HandleTestConnectionAsync();

        lblConnectionStatus = new Label
        {
            Name = "lblConnectionStatus",
            Text = "Status: Not tested",
            Location = new Point(140, 120),
            AutoSize = true,
            ForeColor = Color.DimGray
        };

        var lblModel = new Label { Text = "Model:", Location = new Point(16, 160), AutoSize = true };
        cmbModel = new ComboBox
        {
            Name = "cmbModel",
            Location = new Point(140, 156),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbModel.Items.Add("gpt-image-2");
        cmbModel.SelectedIndex = 0;

        tab.Controls.Add(lblProvider);
        tab.Controls.Add(cmbProvider);
        tab.Controls.Add(lblKey);
        tab.Controls.Add(txtApiKey);
        tab.Controls.Add(btnSaveKey);
        tab.Controls.Add(btnDeleteKey);
        tab.Controls.Add(btnDeleteKeyAlias);
        tab.Controls.Add(btnTestConnection);
        tab.Controls.Add(lblConnectionStatus);
        tab.Controls.Add(lblModel);
        tab.Controls.Add(cmbModel);
    }

    private void BuildGenerationTab(TabPage tab)
    {
        tab.Padding = new Padding(16);

        var lblDirectQuality = new Label { Text = "Direct Image Quality:", Location = new Point(16, 20), AutoSize = true };
        cmbDirectQuality = new ComboBox
        {
            Name = "cmbDirectQuality",
            Location = new Point(220, 16),
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbDirectQuality.Items.AddRange(["low", "medium", "high"]);

        var lblBatchQuality = new Label { Text = "Batch Image Quality:", Location = new Point(16, 60), AutoSize = true };
        cmbBatchQuality = new ComboBox
        {
            Name = "cmbBatchQuality",
            Location = new Point(220, 56),
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbBatchQuality.Items.AddRange(["low", "medium", "high"]);

        var lblStarts = new Label { Text = "Direct starts / minute:", Location = new Point(16, 100), AutoSize = true };
        numStartsPerMinute = new NumericUpDown
        {
            Name = "numStartsPerMinute",
            Location = new Point(220, 96),
            Width = 80,
            Minimum = 1,
            Maximum = 60,
            Value = 5
        };

        var lblConcurrency = new Label { Text = "Max concurrent direct:", Location = new Point(16, 140), AutoSize = true };
        numMaxConcurrency = new NumericUpDown
        {
            Name = "numMaxConcurrency",
            Location = new Point(220, 136),
            Width = 80,
            Minimum = 1,
            Maximum = 20,
            Value = 5
        };

        chkNormalize = new CheckBox
        {
            Name = "chkNormalize",
            Text = "Normalize to manifest resolution (deterministic crop & resize)",
            Location = new Point(16, 180),
            AutoSize = true,
            Checked = true,
            Enabled = false
        };

        tab.Controls.Add(lblDirectQuality);
        tab.Controls.Add(cmbDirectQuality);
        tab.Controls.Add(lblBatchQuality);
        tab.Controls.Add(cmbBatchQuality);
        tab.Controls.Add(lblStarts);
        tab.Controls.Add(numStartsPerMinute);
        tab.Controls.Add(lblConcurrency);
        tab.Controls.Add(numMaxConcurrency);
        tab.Controls.Add(chkNormalize);
    }

    private void BuildAdvancedTab(TabPage tab)
    {
        tab.Padding = new Padding(16);

        var lblPoll = new Label { Text = "Batch poll interval (seconds):", Location = new Point(16, 20), AutoSize = true };
        numBatchPollSeconds = new NumericUpDown
        {
            Name = "numBatchPollSeconds",
            Location = new Point(240, 16),
            Width = 80,
            Minimum = 5,
            Maximum = 300,
            Value = 30
        };

        var lblMaxBatch = new Label { Text = "Max requests per submission:", Location = new Point(16, 60), AutoSize = true };
        numMaxBatchRequests = new NumericUpDown
        {
            Name = "numMaxBatchRequests",
            Location = new Point(240, 56),
            Width = 80,
            Minimum = 1,
            Maximum = 5000,
            Value = 500
        };

        var lblRetry = new Label { Text = "Max direct API attempts:", Location = new Point(16, 100), AutoSize = true };
        numDirectRetryAttempts = new NumericUpDown
        {
            Name = "numDirectRetryAttempts",
            Location = new Point(240, 96),
            Width = 80,
            Minimum = 1,
            Maximum = 10,
            Value = 3
        };

        var lblTimeout = new Label { Text = "HTTP Request Timeout:", Location = new Point(16, 140), AutoSize = true };
        var lblTimeoutVal = new Label { Text = "3 minutes (fixed)", Location = new Point(240, 140), AutoSize = true, ForeColor = Color.DimGray };

        var chkResume = new CheckBox
        {
            Name = "chkResume",
            Text = "Resume batch monitoring on startup",
            Location = new Point(16, 180),
            AutoSize = true,
            Checked = true,
            Enabled = false
        };

        tab.Controls.Add(lblPoll);
        tab.Controls.Add(numBatchPollSeconds);
        tab.Controls.Add(lblMaxBatch);
        tab.Controls.Add(numMaxBatchRequests);
        tab.Controls.Add(lblRetry);
        tab.Controls.Add(numDirectRetryAttempts);
        tab.Controls.Add(lblTimeout);
        tab.Controls.Add(lblTimeoutVal);
        tab.Controls.Add(chkResume);
    }

    public static Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult>? ConfirmBoxProvider { get; set; }
    public static Action<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon>? MessageBoxProvider { get; set; }

    private void ShowMessageBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        if (MessageBoxProvider != null)
        {
            MessageBoxProvider(this, text, caption, buttons, icon);
        }
        else
        {
            MessageBox.Show(this, text, caption, buttons, icon);
        }
    }

    private void LoadSettingsIntoDialog()
    {
        var existingKey = _secretStore.LoadSecret(OpenAiApiKeySecretName);
        var secretExists = !string.IsNullOrWhiteSpace(existingKey);
        txtApiKey.Text = string.Empty;
        txtApiKey.PlaceholderText = secretExists
            ? "●●●●●●●● (Key configured, leave blank to keep)"
            : "sk-...";

        if (secretExists)
        {
            lblConnectionStatus.Text = "Status: Key configured";
            lblConnectionStatus.ForeColor = Color.DarkGreen;
        }

        if (!string.IsNullOrEmpty(_settings.OpenAiModel) && cmbModel.Items.Contains(_settings.OpenAiModel))
        {
            cmbModel.SelectedItem = _settings.OpenAiModel;
        }

        cmbDirectQuality.SelectedItem = _settings.DirectImageQuality;
        if (cmbDirectQuality.SelectedIndex < 0) cmbDirectQuality.SelectedItem = "medium";

        cmbBatchQuality.SelectedItem = _settings.BatchImageQuality;
        if (cmbBatchQuality.SelectedIndex < 0) cmbBatchQuality.SelectedItem = "medium";

        numStartsPerMinute.Value = Math.Clamp(_settings.DirectStartsPerMinute, 1, 60);
        numMaxConcurrency.Value = Math.Clamp(_settings.DirectMaxConcurrency, 1, 20);
        numBatchPollSeconds.Value = Math.Clamp(_settings.BatchPollSeconds, 5, 300);
        numMaxBatchRequests.Value = Math.Clamp(_settings.MaxBatchRequestsPerSubmission, 1, 5000);
        numDirectRetryAttempts.Value = Math.Clamp(_settings.DirectRetryAttempts, 1, 10);
        UpdateApiKeyButtonStates();
    }

    private void UpdateApiKeyButtonStates()
    {
        var hasText = !string.IsNullOrWhiteSpace(txtApiKey.Text);
        var hasStoredKey = !string.IsNullOrWhiteSpace(_secretStore.LoadSecret(OpenAiApiKeySecretName));

        btnSaveKey.Enabled = hasText;
        btnDeleteKey.Enabled = hasStoredKey;
        btnTestConnection.Enabled = hasText || hasStoredKey;
    }

    private void HandleSaveKey()
    {
        var key = txtApiKey.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ShowMessageBox("API Key is empty.", "Invalid Key", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _secretStore.SaveSecret(OpenAiApiKeySecretName, key);
            txtApiKey.Clear();
            txtApiKey.PlaceholderText = "●●●●●●●● (Key configured, leave blank to keep)";
            lblConnectionStatus.Text = "Status: Key saved securely";
            lblConnectionStatus.ForeColor = Color.DarkGreen;
            UpdateApiKeyButtonStates();
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Failed to save key securely: {ex.Message}", "Security Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HandleDeleteKey()
    {
        var confirmResult = ConfirmBoxProvider != null
            ? ConfirmBoxProvider(this, "Are you sure you want to delete the stored API key?", "Delete API Key", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            : MessageBox.Show(this, "Are you sure you want to delete the stored API key?", "Delete API Key", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirmResult != DialogResult.Yes && confirmResult != DialogResult.OK)
        {
            return;
        }

        try
        {
            _secretStore.DeleteSecret(OpenAiApiKeySecretName);
            txtApiKey.Clear();
            txtApiKey.PlaceholderText = "sk-...";
            lblConnectionStatus.Text = "Status: Key deleted";
            lblConnectionStatus.ForeColor = Color.DimGray;
            UpdateApiKeyButtonStates();
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Failed to delete key: {ex.Message}", "Security Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task HandleTestConnectionAsync()
    {
        var key = txtApiKey.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            key = _secretStore.LoadSecret(OpenAiApiKeySecretName)?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(key))
        {
            lblConnectionStatus.Text = "Status: No API key provided";
            lblConnectionStatus.ForeColor = Color.Red;
            return;
        }

        lblConnectionStatus.Text = "Status: Testing connection...";
        lblConnectionStatus.ForeColor = Color.DarkBlue;
        btnTestConnection.Enabled = false;

        OpenAiApiClient? tempClient = null;
        var selectedModel = (string?)cmbModel.SelectedItem ?? "gpt-image-2";
        try
        {
            var client = _apiClient ?? (tempClient = new OpenAiApiClient());
            var success = await client.TestConnectionAsync(key, selectedModel);
            if (success)
            {
                lblConnectionStatus.Text = $"Status: Connected; model '{selectedModel}' available";
                lblConnectionStatus.ForeColor = Color.DarkGreen;
            }
        }
        catch (OpenAiApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            lblConnectionStatus.Text = $"Status: Failed - Model '{selectedModel}' not found or not accessible for this account";
            lblConnectionStatus.ForeColor = Color.Red;
        }
        catch (OpenAiApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            lblConnectionStatus.Text = "Status: Failed - Invalid API key";
            lblConnectionStatus.ForeColor = Color.Red;
        }
        catch (Exception ex)
        {
            lblConnectionStatus.Text = $"Status: Failed - {ex.Message}";
            lblConnectionStatus.ForeColor = Color.Red;
        }
        finally
        {
            tempClient?.Dispose();
            btnTestConnection.Enabled = true;
        }
    }

    private void ApplySettings()
    {
        _settings.OpenAiModel = (string?)cmbModel.SelectedItem ?? "gpt-image-2";
        _settings.DirectImageQuality = (string?)cmbDirectQuality.SelectedItem ?? "medium";
        _settings.BatchImageQuality = (string?)cmbBatchQuality.SelectedItem ?? "medium";
        _settings.DirectStartsPerMinute = (int)numStartsPerMinute.Value;
        _settings.DirectMaxConcurrency = (int)numMaxConcurrency.Value;
        _settings.BatchPollSeconds = (int)numBatchPollSeconds.Value;
        _settings.MaxBatchRequestsPerSubmission = (int)numMaxBatchRequests.Value;
        _settings.DirectRetryAttempts = (int)numDirectRetryAttempts.Value;

        try
        {
            var key = txtApiKey.Text.Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                _secretStore.SaveSecret(OpenAiApiKeySecretName, key);
            }
            // Blank key leaves existing secret untouched; only explicit delete removes it
        }
        catch (Exception ex)
        {
            ShowMessageBox($"Failed to save key securely: {ex.Message}", "Security Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

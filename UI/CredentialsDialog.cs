using NjuPrepaidStatus.Models;

namespace NjuPrepaidStatus.UI;

public sealed class CredentialsDialog : Form
{
    private readonly TextBox _phoneTextBox;
    private readonly TextBox _passwordTextBox;
    private readonly CheckBox _rememberCheckBox;

    public CredentialsDialog(string initialPhone = "", bool rememberDefault = true)
    {
        Text = "Logowanie nju";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 335);
        Padding = new Padding(12);

        var hintLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = "Dodaj konto nju. Mozesz dodac kilka kont i monitorowac je jednoczesnie."
        };

        var phoneLabel = new Label
        {
            AutoSize = true,
            Text = "Numer telefonu:"
        };

        _phoneTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialPhone
        };

        var passLabel = new Label
        {
            AutoSize = true,
            Text = "Haslo:"
        };

        _passwordTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true
        };

        _rememberCheckBox = new CheckBox
        {
            AutoSize = true,
            Checked = rememberDefault,
            Text = "Zapisz dane logowania (DPAPI)"
        };

        var okButton = new Button
        {
            Text = "OK",
            Width = 75,
            Height = 42,
            AutoSize = false,
            DialogResult = DialogResult.OK
        };
        var cancelButton = new Button
        {
            Text = "Anuluj",
            Width = 95,
            Height = 42,
            AutoSize = false,
            DialogResult = DialogResult.Cancel
        };

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };
        buttonsPanel.Controls.Add(cancelButton);
        buttonsPanel.Controls.Add(okButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(hintLabel, 0, 0);
        layout.Controls.Add(phoneLabel, 0, 1);
        layout.Controls.Add(_phoneTextBox, 0, 2);
        layout.Controls.Add(passLabel, 0, 3);
        layout.Controls.Add(_passwordTextBox, 0, 4);
        layout.Controls.Add(_rememberCheckBox, 0, 5);
        layout.Controls.Add(buttonsPanel, 0, 6);

        Controls.Add(layout);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public static CredentialsDialogResult? ShowCredentialsDialog(IWin32Window owner, string initialPhone = "", bool rememberDefault = true)
    {
        using var dialog = new CredentialsDialog(initialPhone, rememberDefault);
        if (((Form)dialog).ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        var phone = dialog._phoneTextBox.Text.Trim();
        var password = dialog._passwordTextBox.Text;
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show(owner, "Numer telefonu i haslo sa wymagane.", "Logowanie", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return new CredentialsDialogResult(new Credentials(phone, password), dialog._rememberCheckBox.Checked);
    }
}

public sealed record CredentialsDialogResult(Credentials Credentials, bool RememberCredentials);

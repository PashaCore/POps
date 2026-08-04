using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace POpsTray
{
    public class FairUseForm : Form
    {
        private Button btnAccept;
        private Label lblTitle;
        private TextBox txtContent;
        private Action _onAccept;

        public FairUseForm(string content, Action onAccept)
        {
            _onAccept = onAccept;
            InitializeComponent(content);
        }

        private void InitializeComponent(string content)
        {
            this.Text = "Kurumsal Adil Kullanım Politikası";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.TopMost = true; // Stay on top until accepted
            this.ControlBox = false; // Prevent closing via X button
            this.BackColor = Color.White;

            lblTitle = new Label
            {
                Text = "Kurumsal Ağ Aydınlatma ve Rıza Metni",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39),
                Location = new Point(20, 20),
                AutoSize = true
            };

            txtContent = new TextBox
            {
                Text = content,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(20, 70),
                Size = new Size(740, 420),
                Font = new Font("Segoe UI", 10),
                BackColor = Color.FromArgb(249, 250, 251),
                BorderStyle = BorderStyle.FixedSingle
            };

            btnAccept = new Button
            {
                Text = "Okudum, Anladım ve Kabul Ediyorum",
                Location = new Point(500, 510),
                Size = new Size(260, 40),
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnAccept.FlatAppearance.BorderSize = 0;
            btnAccept.Click += BtnAccept_Click;

            this.Controls.Add(lblTitle);
            this.Controls.Add(txtContent);
            this.Controls.Add(btnAccept);
        }

        private void BtnAccept_Click(object sender, EventArgs e)
        {
            _onAccept?.Invoke();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}

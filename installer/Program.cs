using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KilliorimInstaller
{
	internal static class Program
	{
		[STAThread]
		private static void Main()
		{
			try
			{
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				Application.Run(new InstallerForm());
			}
			catch (Exception ex)
			{
				MessageBox.Show(
					ex.ToString(),
					"Killiorim Installer - Startup Error",
					MessageBoxButtons.OK,
					MessageBoxIcon.Error);
			}
		}
	}

	internal sealed class InstallerForm : Form
	{
		private const string RepositoryOwner = "yhshh8092-netizen";
		private const string Repository = "VRChatArchiveMod";
		private const string ApiUrl = "https://api.github.com/repos/" + RepositoryOwner + "/" + Repository + "/releases/latest";
		private static readonly HttpClient Http = CreateHttpClient();

		private readonly TextBox _installPath = new TextBox();
		private readonly Label _releaseText = new Label();
		private readonly Label _status = new Label();
		private readonly Button _install = new Button();
		private readonly Button _launch = new Button();
		private readonly Button _refresh = new Button();
		private readonly ProgressBar _progress = new ProgressBar();
		private readonly List<PointF> _stars = new List<PointF>();
		private Image _backgroundImage;
		private MemoryStream _backgroundStream;
		private bool _backgroundAnimationStarted;
		private ReleaseInfo _release;

		private sealed class ReleaseInfo
		{
			public string Version;
			public string DownloadUrl;
			public string Digest;
			public long Size;
		}

		private static HttpClient CreateHttpClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Killiorim-Installer/1.0");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
			return client;
		}

		public InstallerForm()
		{
			Text = "Killiorim Installer";
			ClientSize = new Size(860, 540);
			MinimumSize = new Size(860, 540);
			MaximumSize = new Size(860, 540);
			FormBorderStyle = FormBorderStyle.FixedSingle;
			MaximizeBox = false;
			StartPosition = FormStartPosition.CenterScreen;
			BackColor = Color.FromArgb(8, 10, 18);
			ForeColor = Color.FromArgb(240, 240, 244);
			Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
			DoubleBuffered = true;
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

			LoadBackgroundAnimation();
			GenerateStars();
			BuildInterface();
			Shown += async (_, __) =>
			{
				string detected = FindVrChatInstall();
				if (!string.IsNullOrEmpty(detected)) _installPath.Text = detected;
				UpdateLaunchButtonState();
				await CheckLatestReleaseAsync();
			};
			_installPath.TextChanged += (_, __) => UpdateLaunchButtonState();
		}

		private void GenerateStars()
		{
			_stars.Clear();
			var random = new Random(42);
			for (int i = 0; i < 130; i++)
			{
				_stars.Add(new PointF(random.Next(0, 860), random.Next(0, 540)));
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			base.OnPaintBackground(e);

			if (_backgroundImage == null)
			{
				using (var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(9, 10, 18), Color.FromArgb(18, 21, 31), LinearGradientMode.Vertical))
				{
					e.Graphics.FillRectangle(gradient, ClientRectangle);
				}
			}
			else
			{
				ImageAnimator.UpdateFrames(_backgroundImage);
				float scale = Math.Max(
					(float)ClientSize.Width / _backgroundImage.Width,
					(float)ClientSize.Height / _backgroundImage.Height);
				float width = _backgroundImage.Width * scale;
				float height = _backgroundImage.Height * scale;
				var destination = new RectangleF(
					(ClientSize.Width - width) / 2f,
					(ClientSize.Height - height) / 2f,
					width,
					height);
				e.Graphics.DrawImage(_backgroundImage, Rectangle.Round(destination));

				using (var scrim = new SolidBrush(Color.FromArgb(178, 5, 8, 16)))
				{
					e.Graphics.FillRectangle(scrim, ClientRectangle);
				}
			}

			for (int i = 0; i < _stars.Count; i++)
			{
				var star = _stars[i];
				var alpha = 70 + ((i * 11) % 90);
				using (var dot = new SolidBrush(Color.FromArgb(alpha, 220, 235, 255)))
				{
					e.Graphics.FillEllipse(dot, star.X, star.Y, 2.0f, 2.0f);
				}
			}

			var midpoint = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
			var orbit = new Pen(Color.FromArgb(36, 125, 210, 255), 1f) { DashStyle = DashStyle.Dash };
			for (int i = 0; i < 5; i++)
			{
				var radius = 170 + i * 22;
				e.Graphics.DrawEllipse(orbit, midpoint.X - radius, midpoint.Y - radius, radius * 2, radius * 2);
			}
		}

		private void LoadBackgroundAnimation()
		{
			using (Stream resource = typeof(InstallerForm).Assembly.GetManifestResourceStream("KilliorimInstaller.installer-background.gif"))
			{
				if (resource == null) return;

				using (var buffer = new MemoryStream())
				{
					resource.CopyTo(buffer);
					_backgroundStream = new MemoryStream(buffer.ToArray(), false);
				}
			}

			try
			{
				_backgroundImage = Image.FromStream(_backgroundStream);
				ImageAnimator.Animate(_backgroundImage, OnBackgroundFrameChanged);
				_backgroundAnimationStarted = true;
			}
			catch (Exception ex) when (ex is ArgumentException
				|| ex is ExternalException
				|| ex is IOException
				|| ex is OutOfMemoryException)
			{
				if (_backgroundImage != null)
				{
					_backgroundImage.Dispose();
					_backgroundImage = null;
				}
				_backgroundStream.Dispose();
				_backgroundStream = null;
			}
		}

		private void OnBackgroundFrameChanged(object sender, EventArgs e)
		{
			if (IsDisposed || Disposing || !IsHandleCreated) return;

			try
			{
				BeginInvoke((MethodInvoker)(() =>
				{
					if (!IsDisposed && !Disposing) Invalidate();
				}));
			}
			catch (InvalidOperationException) { }
		}

		private void StopBackgroundAnimation()
		{
			if (_backgroundAnimationStarted && _backgroundImage != null)
			{
				ImageAnimator.StopAnimate(_backgroundImage, OnBackgroundFrameChanged);
				_backgroundAnimationStarted = false;
			}
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			StopBackgroundAnimation();
			base.OnFormClosed(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				StopBackgroundAnimation();
				if (_backgroundImage != null)
				{
					_backgroundImage.Dispose();
					_backgroundImage = null;
				}
				if (_backgroundStream != null)
				{
					_backgroundStream.Dispose();
					_backgroundStream = null;
				}
			}
			base.Dispose(disposing);
		}

		private void BuildInterface()
		{
			var root = new Panel
			{
				Dock = DockStyle.Fill,
				Padding = new Padding(26, 18, 26, 14),
				BackColor = Color.Transparent
			};
			Controls.Add(root);

			var topLine = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = Color.FromArgb(103, 188, 255) };
			Controls.Add(topLine);

			var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.Transparent };
			root.Controls.Add(header);

			var title = new Label
			{
				Text = "KILLIORIM",
				Location = new Point(0, 12),
				Size = new Size(260, 34),
				Font = new Font("Segoe UI", 24f, FontStyle.Bold),
				ForeColor = Color.FromArgb(244, 248, 255),
				AutoSize = false
			};
			header.Controls.Add(title);

			var subtitle = new Label
			{
				Text = "BEPINEX INSTALLER  /  OFFICIAL RELEASE CHANNEL",
				Location = new Point(0, 48),
				Size = new Size(420, 18),
				Font = new Font("Segoe UI", 9f, FontStyle.Bold),
				ForeColor = Color.FromArgb(180, 186, 200),
				AutoSize = false
			};
			header.Controls.Add(subtitle);

			_refresh.Text = "CHECK AGAIN";
			_refresh.Size = new Size(126, 32);
			_refresh.Dock = DockStyle.Right;
			_refresh.Margin = new Padding(0, 22, 0, 0);
			_styleButton(_refresh, false);
			_refresh.Click += async (_, __) => await CheckLatestReleaseAsync();
			header.Controls.Add(_refresh);

			var content = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 2,
				RowCount = 1,
				Margin = new Padding(0, 6, 0, 0),
				BackColor = Color.Transparent
			};
			content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
			content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
			root.Controls.Add(content);

			var installCard = new Panel
			{
				Dock = DockStyle.Fill,
				Margin = new Padding(0, 0, 12, 0),
				Padding = new Padding(16, 16, 16, 14),
				BackColor = Color.FromArgb(15, 18, 27),
				BorderStyle = BorderStyle.None
			};
			installCard.Paint += (_, paintArgs) =>
			{
				using (var p = new Pen(Color.FromArgb(120, 133, 220, 255), 1.2f))
				{
					paintArgs.Graphics.DrawRectangle(p, 0, 0, installCard.Width - 1, installCard.Height - 1);
				}
			};
			content.Controls.Add(installCard, 0, 0);

			var installHeader = new Label
			{
				Text = "PRIMARY INSTALL",
				AutoSize = true,
				Font = new Font("Segoe UI", 8f, FontStyle.Bold),
				ForeColor = Color.FromArgb(193, 223, 255),
				Padding = new Padding(0),
				Margin = new Padding(0, 0, 0, 10)
			};
			installCard.Controls.Add(installHeader);

			var pathLabel = new Label
			{
				Text = "VRCHAT INSTALL LOCATION",
				AutoSize = true,
				Font = new Font("Segoe UI", 8f, FontStyle.Bold),
				ForeColor = Color.FromArgb(180, 186, 200),
				Margin = new Padding(0, 12, 0, 6)
			};
			installCard.Controls.Add(pathLabel);

			var pathRow = new TableLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 36,
				ColumnCount = 3,
				RowCount = 1,
				BackColor = Color.Transparent,
				Margin = new Padding(0, 0, 0, 0)
			};
			pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
			pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
			pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98F));
			installCard.Controls.Add(pathRow);

			_installPath.Dock = DockStyle.Fill;
			_installPath.BackColor = Color.FromArgb(18, 20, 30);
			_installPath.ForeColor = Color.White;
			_installPath.BorderStyle = BorderStyle.FixedSingle;
			_installPath.Font = new Font("Segoe UI", 9.5f);
			_installPath.Margin = new Padding(0, 0, 8, 0);
			pathRow.Controls.Add(_installPath, 0, 0);

			var browse = new Button { Text = "BROWSE" };
			_styleButton(browse, false);
			browse.Dock = DockStyle.Fill;
			browse.Margin = new Padding(0, 0, 8, 0);
			browse.Click += BrowseForInstallFolder;
			pathRow.Controls.Add(browse, 1, 0);

			var detect = new Button { Text = "AUTO FIND" };
			_styleButton(detect, false);
			detect.Dock = DockStyle.Fill;
			detect.Margin = new Padding(0);
			detect.Click += (_, __) =>
			{
				string found = FindVrChatInstall();
				if (string.IsNullOrEmpty(found)) SetStatus("VRChat was not found. Browse to its install folder.", true);
				else { _installPath.Text = found; SetStatus("VRChat installation found.", false); }
			};
			pathRow.Controls.Add(detect, 2, 0);

			_install.Text = "DOWNLOAD  &  INSTALL LATEST DLL";
			_install.Dock = DockStyle.Top;
			_install.Height = 58;
			_install.Margin = new Padding(0, 18, 0, 0);
			_install.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
			_install.FlatStyle = FlatStyle.Flat;
			_install.FlatAppearance.BorderColor = Color.FromArgb(110, 194, 255);
			_install.FlatAppearance.BorderSize = 1;
			_install.BackColor = Color.FromArgb(14, 25, 36);
			_install.ForeColor = Color.FromArgb(235, 247, 255);
			_install.Enabled = false;
			_install.Click += async (_, __) => await DownloadAndInstallAsync();
			installCard.Controls.Add(_install);

			var installDetail = new Label
			{
				Text = "BEPINEX 6 / IL2CPP   •   Existing DLLs are backed up before replacement",
				Dock = DockStyle.Top,
				Height = 18,
				Margin = new Padding(0, 12, 0, 0),
				Font = new Font("Segoe UI", 8f, FontStyle.Regular),
				ForeColor = Color.FromArgb(141, 150, 175),
				AutoEllipsis = true
			};
			installCard.Controls.Add(installDetail);

			var right = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 2,
				BackColor = Color.Transparent,
				Margin = new Padding(12, 0, 0, 0)
			};
			right.RowStyles.Add(new RowStyle(SizeType.Percent, 52F));
			right.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
			content.Controls.Add(right, 1, 0);

			var releaseCard = new Panel
			{
				Dock = DockStyle.Fill,
				Margin = new Padding(0, 0, 0, 10),
				Padding = new Padding(16, 14, 16, 12),
				BackColor = Color.FromArgb(90, 16, 20, 28),
				BorderStyle = BorderStyle.None
			};
			releaseCard.Paint += (_, paintArgs) =>
			{
				using (var p = new Pen(Color.FromArgb(120, 142, 225, 255), 1.5f))
				{
					paintArgs.Graphics.DrawRectangle(p, 0, 0, releaseCard.Width - 1, releaseCard.Height - 1);
				}
				var indicator = new Rectangle(16, 15, 10, 10);
				paintArgs.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(80, 118, 219, 255)), indicator);
				paintArgs.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(180, 150, 245, 255)), indicator.X + 2, indicator.Y + 2, 6, 6);
			};
			right.Controls.Add(releaseCard, 0, 0);

			var releaseHeader = new TableLayoutPanel
			{
				Dock = DockStyle.Top,
				Height = 24,
				ColumnCount = 2,
				RowCount = 1,
				BackColor = Color.Transparent
			};
			releaseHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
			releaseHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
			var releaseCaption = new Label
			{
				Text = "LATEST PUBLISHED BUILD",
				Dock = DockStyle.Fill,
				Font = new Font("Segoe UI", 8f, FontStyle.Bold),
				ForeColor = Color.FromArgb(255, 255, 255),
				AutoSize = false,
				Padding = new Padding(8, 0, 0, 0)
			};
			var releaseOnline = new Label
			{
				Text = "LIVE",
				AutoSize = true,
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				Font = new Font("Segoe UI", 7f, FontStyle.Bold),
				ForeColor = Color.FromArgb(155, 215, 255),
				TextAlign = ContentAlignment.MiddleRight
			};
			releaseHeader.Controls.Add(releaseCaption, 0, 0);
			releaseHeader.Controls.Add(releaseOnline, 1, 0);
			releaseCard.Controls.Add(releaseHeader);

			_releaseText.Text = "Checking GitHub releases...";
			_releaseText.Dock = DockStyle.Fill;
			_releaseText.Margin = new Padding(8, 16, 0, 0);
			_releaseText.Font = new Font("Segoe UI", 11f, FontStyle.Regular);
			_releaseText.ForeColor = Color.FromArgb(233, 241, 255);
			_releaseText.AutoEllipsis = true;
			releaseCard.Controls.Add(_releaseText);

			var launchCard = new Panel
			{
				Dock = DockStyle.Fill,
				Margin = new Padding(0),
				Padding = new Padding(16, 14, 16, 12),
				BackColor = Color.FromArgb(18, 20, 29),
				BorderStyle = BorderStyle.None
			};
			launchCard.Paint += (_, paintArgs) =>
			{
				using (var p = new Pen(Color.FromArgb(120, 92, 125, 255), 1.2f))
				{
					paintArgs.Graphics.DrawRectangle(p, 0, 0, launchCard.Width - 1, launchCard.Height - 1);
				}
			};
			right.Controls.Add(launchCard, 0, 1);

			var launchContent = new TableLayoutPanel
			{
				Dock = DockStyle.Fill,
				ColumnCount = 1,
				RowCount = 3,
				BackColor = Color.Transparent
			};
			launchContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			launchContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
			launchContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
			launchCard.Controls.Add(launchContent);

			var launchHeader = new Label
			{
				Text = "LAUNCH STATUS",
				AutoSize = true,
				Font = new Font("Segoe UI", 8f, FontStyle.Bold),
				ForeColor = Color.FromArgb(193, 223, 255),
				Margin = new Padding(0, 0, 0, 6)
			};
			launchContent.Controls.Add(launchHeader, 0, 0);

			var launchCue = new Label
			{
				Text = "VRChat must be closed while the plugin is installed.",
				Dock = DockStyle.Fill,
				Margin = new Padding(0, 4, 0, 10),
				Font = new Font("Segoe UI", 9f, FontStyle.Regular),
				ForeColor = Color.FromArgb(192, 196, 214),
				AutoEllipsis = true
			};
			launchContent.Controls.Add(launchCue, 0, 1);

			_launch.Text = "LAUNCH VRCHAT";
			_launch.Dock = DockStyle.Top;
			_launch.Height = 42;
			_launch.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
			_launch.FlatStyle = FlatStyle.Flat;
			_launch.FlatAppearance.BorderColor = Color.FromArgb(92, 102, 124);
			_launch.FlatAppearance.BorderSize = 1;
			_launch.BackColor = Color.FromArgb(18, 22, 31);
			_launch.ForeColor = Color.FromArgb(232, 238, 245);
			_launch.Enabled = false;
			_launch.Click += LaunchVrChat;
			launchContent.Controls.Add(_launch, 0, 2);

			var bottom = new Panel { Dock = DockStyle.Bottom, Height = 90, BackColor = Color.Transparent };
			root.Controls.Add(bottom);

			_progress.Dock = DockStyle.Top;
			_progress.Height = 8;
			_progress.Style = ProgressBarStyle.Continuous;
			_progress.Visible = false;
			bottom.Controls.Add(_progress);

			_status.Text = "The game must be closed while the plugin is installed.";
			_status.Dock = DockStyle.Top;
			_status.Height = 30;
			_status.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
			_status.ForeColor = Color.FromArgb(194, 190, 197);
			_status.BackColor = Color.Transparent;
			_status.Padding = new Padding(0);
			_status.AutoEllipsis = true;
			_status.TextAlign = ContentAlignment.MiddleLeft;
			bottom.Controls.Add(_status);

			var footer = new Label
			{
				Text = "BEPINEX 6  /  IL2CPP     •     Existing DLLs are backed up before replacement",
				Dock = DockStyle.Top,
				Height = 18,
				Font = new Font("Segoe UI", 8f, FontStyle.Regular),
				ForeColor = Color.FromArgb(130, 127, 134),
				AutoEllipsis = true
			};
			bottom.Controls.Add(footer);
		}

		private static void _styleButton(Button button, bool primary)
		{
			button.FlatStyle = FlatStyle.Flat;
			button.FlatAppearance.BorderColor = primary ? Color.FromArgb(110, 194, 255) : Color.FromArgb(74, 82, 103);
			button.FlatAppearance.BorderSize = 1;
			button.BackColor = primary ? Color.FromArgb(18, 28, 38) : Color.FromArgb(17, 22, 31);
			button.ForeColor = primary ? Color.FromArgb(234, 246, 255) : Color.FromArgb(237, 234, 239);
			button.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
			button.Cursor = Cursors.Hand;
		}

		private void UpdateLaunchButtonState()
		{
			string exe = ResolveVrChatExecutable(_installPath.Text);
			bool canLaunch = !string.IsNullOrEmpty(exe) && File.Exists(exe);
			_launch.Enabled = canLaunch;
			_launch.BackColor = canLaunch ? Color.FromArgb(19, 35, 58) : Color.FromArgb(18, 18, 24);
			_launch.ForeColor = canLaunch ? Color.FromArgb(230, 240, 255) : Color.FromArgb(110, 115, 130);
			_launch.FlatAppearance.BorderColor = canLaunch ? Color.FromArgb(128, 170, 255) : Color.FromArgb(72, 79, 92);
			_launch.Text = canLaunch ? "LAUNCH VRCHAT" : "SELECT VRCHAT FOLDER TO ENABLE LAUNCH";
		}

		private void LaunchVrChat(object sender, EventArgs e)
		{
			string exe = ResolveVrChatExecutable(_installPath.Text);
			if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
			{
				SetStatus("Choose the VRChat install folder first.", true);
				return;
			}

			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = exe,
					WorkingDirectory = Path.GetDirectoryName(exe),
					UseShellExecute = true
				});
				SetStatus("Launching VRChat...", false);
			}
			catch (Exception ex)
			{
				SetStatus("Launch failed: " + ex.Message, true);
			}
		}

		private async Task CheckLatestReleaseAsync()
		{
			_refresh.Enabled = false;
			_install.Enabled = false;
			_release = null;
			_releaseText.Text = "Checking GitHub releases...";
			SetStatus("Connecting to the public release page...", false);
			try
			{
				using (HttpResponseMessage response = await Http.GetAsync(ApiUrl))
				{
					if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
					{
						_releaseText.Text = "No published release found yet.";
						SetStatus("The installer will work once a release with Killiorim.dll is published.", true);
						return;
					}
					response.EnsureSuccessStatusCode();
					var root = new JavaScriptSerializer().DeserializeObject(await response.Content.ReadAsStringAsync())
						as Dictionary<string, object>;
					if (root == null) throw new InvalidDataException("GitHub returned invalid release metadata.");
					string version = ReadString(root, "tag_name") ?? "latest";
					if (!root.TryGetValue("assets", out object assetsValue) || !(assetsValue is object[] assets))
						throw new InvalidDataException("The release has no downloadable assets.");

					foreach (object assetValue in assets)
					{
						var asset = assetValue as Dictionary<string, object>;
						if (asset == null || ReadString(asset, "name") != "Killiorim.dll") continue;
						object sizeValue;
						long size = 0;
						if (asset.TryGetValue("size", out sizeValue)) long.TryParse(sizeValue.ToString(), out size);
						_release = new ReleaseInfo
						{
							Version = version,
							DownloadUrl = ReadString(asset, "browser_download_url"),
							Size = size,
							Digest = ReadString(asset, "digest")
						};
						break;
					}
				}
				if (_release == null)
				{
					_releaseText.Text = "Latest release is missing Killiorim.dll.";
					SetStatus("Ask the maintainer to attach Killiorim.dll to the published release.", true);
					return;
				}
				if (!IsTrustedDownloadUrl(_release.DownloadUrl))
					throw new InvalidDataException("The release asset URL is not from the official repository.");

				string sizeText = _release.Size > 0 ? "  /  " + FormatSize(_release.Size) : "";
				_releaseText.Text = _release.Version + "  /   KILLIORIM.DLL" + sizeText;
				SetStatus("Latest build is ready to install.", false);
				_install.Enabled = true;
			}
			catch (Exception ex)
			{
				_releaseText.Text = "Could not check for a release.";
				SetStatus("Release check failed: " + ex.Message, true);
			}
			finally { _refresh.Enabled = true; }
		}

		private async Task DownloadAndInstallAsync()
		{
			if (_release == null) return;
			if (Process.GetProcessesByName("VRChat").Length > 0)
			{
				SetStatus("Close VRChat before installing; its DLL may be locked while running.", true);
				return;
			}

			string pluginDirectory;
			try { pluginDirectory = ResolvePluginDirectory(_installPath.Text); }
			catch (Exception ex) { SetStatus(ex.Message, true); return; }

			_install.Enabled = false;
			_refresh.Enabled = false;
			_progress.Visible = true;
			_progress.Style = ProgressBarStyle.Marquee;
			SetStatus("Downloading " + _release.Version + " from GitHub...", false);
			string temporaryFile = null;
			try
			{
				byte[] data = await Http.GetByteArrayAsync(_release.DownloadUrl);
				if (data.Length < 1024 || data[0] != (byte)'M' || data[1] != (byte)'Z')
					throw new InvalidDataException("The downloaded file is not a valid Windows DLL.");
				if (_release.Size > 0 && data.LongLength != _release.Size)
					throw new InvalidDataException("The downloaded file size does not match GitHub's release metadata.");
				if (!string.IsNullOrEmpty(_release.Digest))
				{
					string expected = _release.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
						? _release.Digest.Substring(7) : _release.Digest;
					string actual;
					using (SHA256 sha = SHA256.Create())
						actual = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
					if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("SHA-256 verification failed; the existing install was not changed.");
				}

				Directory.CreateDirectory(pluginDirectory);
				string destination = Path.Combine(pluginDirectory, "Killiorim.dll");
				temporaryFile = Path.Combine(pluginDirectory, "Killiorim.dll.download");
				File.WriteAllBytes(temporaryFile, data);
				if (File.Exists(destination))
				{
					string backup = destination + ".backup";
					if (File.Exists(backup)) File.Delete(backup);
					File.Replace(temporaryFile, destination, backup, true);
					temporaryFile = null;
					SetStatus("Installed " + _release.Version + ". Previous DLL saved as Killiorim.dll.backup.", false);
				}
				else
				{
					File.Move(temporaryFile, destination);
					temporaryFile = null;
					SetStatus("Installed " + _release.Version + " to BepInEx/plugins.", false);
				}
				UpdateLaunchButtonState();
				MessageBox.Show(this, "Killiorim " + _release.Version + " is installed. Restart VRChat to load it.",
					"Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			catch (Exception ex) { SetStatus("Install failed: " + ex.Message, true); }
			finally
			{
				if (temporaryFile != null) { try { File.Delete(temporaryFile); } catch { } }
				_progress.Visible = false;
				_refresh.Enabled = true;
				_install.Enabled = _release != null;
				UpdateLaunchButtonState();
			}
		}

		private void BrowseForInstallFolder(object sender, EventArgs e)
		{
			using (var dialog = new FolderBrowserDialog())
			{
				dialog.Description = "Select the VRChat installation folder (the one containing VRChat.exe).";
				dialog.ShowNewFolderButton = false;
				if (Directory.Exists(_installPath.Text)) dialog.SelectedPath = _installPath.Text;
				if (dialog.ShowDialog(this) == DialogResult.OK) _installPath.Text = dialog.SelectedPath;
			}
		}

		private static string ResolvePluginDirectory(string selectedPath)
		{
			if (string.IsNullOrWhiteSpace(selectedPath))
				throw new DirectoryNotFoundException("Choose your VRChat installation folder first.");
			string path = Path.GetFullPath(selectedPath.Trim().Trim('"'));
			string leaf = new DirectoryInfo(path).Name;
			if (string.Equals(leaf, "plugins", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(new DirectoryInfo(path).Parent?.Name, "BepInEx", StringComparison.OrdinalIgnoreCase))
				return path;
			if (string.Equals(leaf, "BepInEx", StringComparison.OrdinalIgnoreCase))
				return Path.Combine(path, "plugins");
			if (!File.Exists(Path.Combine(path, "VRChat.exe")))
				throw new DirectoryNotFoundException("Choose the VRChat folder containing VRChat.exe, or its BepInEx/plugins folder.");
			string bepinex = Path.Combine(path, "BepInEx");
			if (!Directory.Exists(bepinex))
				throw new DirectoryNotFoundException("BepInEx was not found in this folder. Install BepInEx 6 IL2CPP first.");
			return Path.Combine(bepinex, "plugins");
		}

		private static string ResolveVrChatExecutable(string selectedPath)
		{
			if (string.IsNullOrWhiteSpace(selectedPath)) return null;
			string root = selectedPath.Trim().Trim('"');
			if (File.Exists(Path.Combine(root, "VRChat.exe"))) return Path.Combine(root, "VRChat.exe");
			var folder = new DirectoryInfo(root);
			if (folder.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase) && folder.Parent != null)
			{
				string parent = folder.Parent.FullName;
				if (File.Exists(Path.Combine(parent, "VRChat.exe"))) return Path.Combine(parent, "VRChat.exe");
			}
			if (folder.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && folder.Parent != null)
			{
				string parent = folder.Parent.FullName;
				if (File.Exists(Path.Combine(parent, "VRChat.exe"))) return Path.Combine(parent, "VRChat.exe");
			}
			return File.Exists(Path.Combine(root, "VRChat.exe")) ? Path.Combine(root, "VRChat.exe") : null;
		}

		private static string FindVrChatInstall()
		{
			var steamRoots = new List<string>();
			try
			{
				object steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
				if (steamPath is string value && !string.IsNullOrWhiteSpace(value)) steamRoots.Add(value.Replace('/', '\\'));
			}
			catch { }
			try
			{
				string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
				string standard = Path.Combine(programFiles, "Steam");
				if (Directory.Exists(standard)) steamRoots.Add(standard);
			}
			catch { }

			var libraries = new List<string>(steamRoots);
			foreach (string root in steamRoots)
			{
				string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
				if (!File.Exists(libraryFile)) continue;
				try
				{
					string contents = File.ReadAllText(libraryFile);
					foreach (Match match in Regex.Matches(contents, "\\\"path\\\"\\s*\\\"([^\\\"]+)\\\""))
						libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
				}
				catch { }
			}

			foreach (string library in libraries)
			{
				string game = Path.Combine(library, "steamapps", "common", "VRChat");
				if (File.Exists(Path.Combine(game, "VRChat.exe")) && Directory.Exists(Path.Combine(game, "BepInEx")))
					return game;
			}
			return null;
		}

		private static bool IsTrustedDownloadUrl(string value)
		{
			return Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
				&& uri.Scheme == Uri.UriSchemeHttps
				&& string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
				&& uri.AbsolutePath.StartsWith("/" + RepositoryOwner + "/" + Repository + "/releases/download/", StringComparison.OrdinalIgnoreCase);
		}

		private static string ReadString(Dictionary<string, object> values, string key)
		{
			object value;
			return values != null && values.TryGetValue(key, out value) ? value as string : null;
		}

		private static string FormatSize(long bytes)
		{
			return (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
		}

		private void SetStatus(string text, bool error)
		{
			_status.Text = text;
			_status.ForeColor = error ? Color.FromArgb(255, 150, 160) : Color.FromArgb(194, 190, 197);
			_status.MaximumSize = new Size(640, 0);
			_status.AutoEllipsis = false;
		}
	}
}

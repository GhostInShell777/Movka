using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Lame;

namespace MovkaApp
{
    // Клас для логічного керування синтезом та паузами
    public class VoiceService
    {
        public void GenerateMp3(string text, string outputPath)
        {
            // 1. Розбиваємо текст на частини, зберігаючи знаки пунктуації
            var parts = Regex.Split(text, @"(?<=[.!,?;…])")
                             .Where(s => !string.IsNullOrWhiteSpace(s))
                             .ToList();

            string tempFolder = Path.Combine(Path.GetTempPath(), "MovkaTemp_" + Guid.NewGuid());
            Directory.CreateDirectory(tempFolder);

            try
            {
                var waveFiles = new List<string>();

                using (var synth = new SpeechSynthesizer())
                {
                    var voice = synth.GetInstalledVoices()
                        .FirstOrDefault(v => v.VoiceInfo.Name.Contains("Natalia"))
                        ?? throw new Exception("Голос 'Natalia' не знайдено! Перевірте установку RHVoice.");

                    synth.SelectVoice(voice.VoiceInfo.Name);
                    synth.Rate = 0;

                    for (int i = 0; i < parts.Count; i++)
                    {
                        string partText = parts[i].Trim();
                        if (string.IsNullOrEmpty(partText)) continue;

                        string partFile = Path.Combine(tempFolder, $"part_{i}.wav");

                        // Генеруємо озвучку речення/фрази
                        synth.SetOutputToWaveFile(partFile, new SpeechAudioFormatInfo(22050, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                        synth.Speak(partText);
                        synth.SetOutputToNull();
                        waveFiles.Add(partFile);

                        // --- ТАЙМІНГИ ПАУЗ ---
                        double pauseDuration = 0.1;

                        if (partText.EndsWith(".") || partText.EndsWith("!") || partText.EndsWith("?"))
                            pauseDuration = 0.5; // 0.5 секунди для крапки

                        else if (partText.EndsWith("…"))
                            pauseDuration = 1.2;

                        else if (partText.EndsWith(","))
                            pauseDuration = 0.2; // 0.2 секунди для коми

                        else if (partText.EndsWith(";") || partText.EndsWith(":"))
                            pauseDuration = 0.4;

                        string silenceFile = Path.Combine(tempFolder, $"silence_{i}.wav");
                        CreateSilence(silenceFile, pauseDuration, 22050);
                        waveFiles.Add(silenceFile);
                    }
                }

                CombineFilesToMp3(waveFiles, outputPath);
            }
            finally
            {
                if (Directory.Exists(tempFolder))
                    try { Directory.Delete(tempFolder, true); } catch { }
            }
        }

        private void CreateSilence(string path, double durationSeconds, int sampleRate)
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1)))
            {
                byte[] silence = new byte[(int)(sampleRate * 2 * durationSeconds)];
                writer.Write(silence, 0, silence.Length);
            }
        }

        private void CombineFilesToMp3(List<string> files, string outputPath)
        {
            using (var writer = new LameMP3FileWriter(outputPath, new WaveFormat(22050, 16, 1), 128))
            {
                foreach (var file in files)
                {
                    if (!File.Exists(file)) continue;
                    using (var reader = new WaveFileReader(file))
                    {
                        reader.CopyTo(writer);
                    }
                }
            }
        }
    }

    // Клас плеєра
    public class SimplePlayer : IDisposable
    {
        private AudioFileReader? _reader;
        private WaveOutEvent _output = new WaveOutEvent();
        public event EventHandler? PlaybackStopped;

        public SimplePlayer() => _output.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, EventArgs.Empty);

        public void Load(string path)
        {
            Stop();
            _reader?.Dispose();
            _reader = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _reader = new AudioFileReader(path);
                _output.Init(_reader);
            }
        }

        public void Play() => _output.Play();
        public void Pause() => _output.Pause();
        public void Stop() => _output.Stop();

        public void Seek(double seconds)
        {
            if (_reader == null) return;
            var newTime = _reader.CurrentTime.Add(TimeSpan.FromSeconds(seconds));
            if (newTime < TimeSpan.Zero) _reader.CurrentTime = TimeSpan.Zero;
            else if (newTime > _reader.TotalTime) _reader.CurrentTime = _reader.TotalTime;
            else _reader.CurrentTime = newTime;
        }

        public void SetPosition(double seconds)
        {
            if (_reader != null)
            {
                if (seconds < 0) _reader.CurrentTime = TimeSpan.Zero;
                else if (seconds > _reader.TotalTime.TotalSeconds) _reader.CurrentTime = _reader.TotalTime;
                else _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
            }
        }

        public PlaybackState State => _output.PlaybackState;
        public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan TotalTime => _reader?.TotalTime ?? TimeSpan.Zero;
        public double Progress => _reader?.CurrentTime.TotalSeconds ?? 0;
        public void Dispose() { _output?.Dispose(); _reader?.Dispose(); }
    }

    // Головне вікно
    public class MainWindow : Form
    {
        private readonly VoiceService _voiceService = new();
        private readonly SimplePlayer _player = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
        private bool _isDragging = false;

        private TextBox txtInput = new();
        private Button btnGo = new();
        private TrackBar track = new();
        private Label lblTime = new();
        private Button btnPlayPause = new();
        private Button btnReplay = new();
        private Button btnBack10 = new();
        private Button btnForward10 = new();

        public MainWindow()
        {
            this.Text = "Movka - Текст у голос (HQ)";
            this.Size = new Size(550, 650);
            this.MinimumSize = new Size(450, 550);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;

            SetupLayout();
            CheckVoiceInstallation();

            _timer.Tick += (s, e) => { if (!_isDragging) UpdateUI(); };

            _player.PlaybackStopped += (s, e) => {
                if (this.IsHandleCreated && !this.IsDisposed)
                {
                    this.BeginInvoke(new Action(() => {
                        btnPlayPause.Text = "▶";
                        _timer.Stop();
                        UpdateUI();
                    }));
                }
            };
        }

        private void CheckVoiceInstallation()
        {
            try
            {
                using (var synth = new SpeechSynthesizer())
                {
                    var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Name.Contains("Natalia"));
                    if (voice == null) MessageBox.Show("Голос 'Natalia' не знайдено. Переконайтеся, що RHVoice встановлено.");
                }
            }
            catch { }
        }

        private void SetupLayout()
        {
            Panel pnlTextContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            txtInput.Multiline = true;
            txtInput.Dock = DockStyle.Fill;
            txtInput.Font = new Font("Segoe UI", 10); // Повернуто розмір 10
            txtInput.ScrollBars = ScrollBars.Vertical;
            pnlTextContainer.Controls.Add(txtInput);

            Panel pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 210, Padding = new Padding(15) };
            GroupBox groupMain = new GroupBox { Text = "Керування", Dock = DockStyle.Top, Height = 140 };

            btnGo.Text = "Згенерувати MP3";
            btnGo.Height = 35;
            btnGo.Dock = DockStyle.Top;
            btnGo.Click += BtnGo_Click;

            track.Dock = DockStyle.Top;
            track.Height = 45;
            track.TickStyle = TickStyle.None;
            track.Enabled = false;
            track.MouseDown += (s, e) => _isDragging = true;
            track.MouseUp += (s, e) => {
                _isDragging = false;
                if (track.Enabled) _player.SetPosition(track.Value);
            };

            lblTime.Text = "00:00 / 00:00";
            lblTime.Dock = DockStyle.Top;
            lblTime.TextAlign = ContentAlignment.MiddleCenter;
            lblTime.Font = new Font("Consolas", 10, FontStyle.Bold);

            groupMain.Controls.Add(lblTime);
            groupMain.Controls.Add(track);
            groupMain.Controls.Add(btnGo);

            FlowLayoutPanel pnlButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
            btnPlayPause.Text = "▶"; btnPlayPause.Size = new Size(50, 35);
            btnPlayPause.Click += (s, e) => {
                if (_player.State == PlaybackState.Playing) { _player.Pause(); btnPlayPause.Text = "▶"; }
                else if (track.Enabled) { _player.Play(); btnPlayPause.Text = "Ⅱ"; _timer.Start(); }
            };

            btnReplay.Text = "↻"; btnReplay.Size = new Size(50, 35);
            btnReplay.Click += (s, e) => { _player.SetPosition(0); UpdateUI(); };

            btnBack10.Text = "-10s"; btnBack10.Size = new Size(60, 35);
            btnBack10.Click += (s, e) => { _player.Seek(-10); UpdateUI(); };

            btnForward10.Text = "+10s"; btnForward10.Size = new Size(60, 35);
            btnForward10.Click += (s, e) => { _player.Seek(10); UpdateUI(); };

            pnlButtons.Controls.AddRange(new Control[] { btnPlayPause, btnReplay, btnBack10, btnForward10 });
            pnlBottom.Controls.Add(groupMain);
            pnlBottom.Controls.Add(pnlButtons);

            this.Controls.Add(pnlTextContainer);
            this.Controls.Add(pnlBottom);
        }

        private void BtnGo_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtInput.Text)) return;
            var sfd = new SaveFileDialog { Filter = "MP3|*.mp3", FileName = "speech_output.mp3" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    _timer.Stop();
                    btnGo.Enabled = false;
                    lblTime.Text = "Обробка...";
                    lblTime.ForeColor = Color.DarkOrange;
                    this.Refresh();

                    _player.Load("");
                    _voiceService.GenerateMp3(txtInput.Text, sfd.FileName);
                    _player.Load(sfd.FileName);

                    track.Maximum = (int)_player.TotalTime.TotalSeconds;
                    track.Value = 0;
                    track.Enabled = true;

                    lblTime.ForeColor = Color.Black;
                    lblTime.Text = $"00:00 / {_player.TotalTime:mm\\:ss}";
                }
                catch (Exception ex)
                {
                    lblTime.Text = "Помилка!";
                    lblTime.ForeColor = Color.Red;
                    MessageBox.Show("Помилка: " + ex.Message);
                }
                finally { btnGo.Enabled = true; }
            }
        }

        private void UpdateUI()
        {
            if (this.IsDisposed) return;
            if (_player.State != PlaybackState.Stopped)
            {
                int currentPos = (int)_player.Progress;
                if (currentPos >= track.Minimum && currentPos <= track.Maximum) track.Value = currentPos;
                lblTime.Text = $"{_player.CurrentTime:mm\\:ss} / {_player.TotalTime:mm\\:ss}";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { _player.Dispose(); base.OnFormClosing(e); }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }
}
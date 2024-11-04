﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Notification;

namespace TMSpeech.Core
{
    public enum JobStatus
    {
        Stopped,
        Running,
        Paused,
    }

    public static class JobControllerFactory
    {
        private static Lazy<JobManager> _instance = new(() => new JobManagerImpl());
        public static JobManager Instance => _instance.Value;
    }

    public abstract class JobManager
    {
        private JobStatus _status;

        public JobStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                StatusChanged?.Invoke(this, value);
            }
        }

        public long RunningSeconds { get; protected set; }

        public event EventHandler<JobStatus> StatusChanged;
        public event EventHandler<SpeechEventArgs> TextChanged;
        public event EventHandler<SpeechEventArgs> SentenceDone;
        public event EventHandler<long> RunningSecondsChanged;

        protected void OnTextChanged(SpeechEventArgs e) => TextChanged?.Invoke(this, e);
        protected void OnSentenceDone(SpeechEventArgs e) => SentenceDone?.Invoke(this, e);
        protected void OnUpdateRunningSeconds(long seconds) => RunningSecondsChanged?.Invoke(this, seconds);

        public abstract void Start();
        public abstract void Pause();
        public abstract void Stop();
    }

    public class JobManagerImpl : JobManager
    {
        private readonly PluginManager _pluginManager;


        internal JobManagerImpl()
        {
            _pluginManager = PluginManagerFactory.GetInstance();
        }

        private IAudioSource? _audioSource;
        private IRecognizer? _recognizer;

        private void InitAudioSource()
        {
            var configAudioSource = ConfigManagerFactory.Instance.Get<string>("audio.source");
            var config = ConfigManagerFactory.Instance.Get<string>($"plugin.{configAudioSource}.config");

            _audioSource = _pluginManager.AudioSources.FirstOrDefault(x => x.Name == configAudioSource);
            if (_audioSource != null)
            {
                _audioSource.LoadConfig(config);
                _audioSource.DataAvailable += OnAudioSourceOnDataAvailable;
                _audioSource.ExceptionOccured += OnPluginRunningExceptionOccurs;
            }
        }

        private Timer? _timer;


        private void OnAudioSourceOnDataAvailable(object? o, byte[] data)
        {
            // Console.WriteLine(o?.GetHashCode().ToString("x8") ?? "<null>");
            _recognizer?.Feed(data);
        }

        private void InitRecognizer()
        {
            var configRecognizer = ConfigManagerFactory.Instance.Get<string>("recognizer.source");
            var config = ConfigManagerFactory.Instance.Get<string>($"plugin.{configRecognizer}.config");
            _recognizer = _pluginManager.Recognizers.FirstOrDefault(x => x.Name == configRecognizer);

            if (_recognizer != null)
            {
                _recognizer.LoadConfig(config);
                _recognizer.TextChanged += OnRecognizerOnTextChanged;
                _recognizer.SentenceDone += OnRecognizerOnSentenceDone;
                _recognizer.ExceptionOccured += OnPluginRunningExceptionOccurs;
            }
        }

        private void OnRecognizerOnSentenceDone(object? sender, SpeechEventArgs args)
        {
            OnSentenceDone(args);
        }

        private void OnRecognizerOnTextChanged(object? sender, SpeechEventArgs args)
        {
            OnTextChanged(args);
        }

        private void StartRecognize()
        {
            InitAudioSource();
            InitRecognizer();

            if (_audioSource == null || _recognizer == null)
            {
                Status = JobStatus.Stopped;
                NotificationManager.Instance.Notify("语音源或识别器初始化失败", "启动失败", NotificationType.Error);
                return;
            }


            try
            {
                _recognizer.Start();
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"识别器启动失败：{ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }

            try
            {
                _audioSource.Start();
            }
            catch (Exception ex)
            {
                _recognizer.Stop();
                NotificationManager.Instance.Notify($"语音源启动失败 {ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }

            if (Status == JobStatus.Stopped) RunningSeconds = 0;

            Status = JobStatus.Running;

            _timer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void OnPluginRunningExceptionOccurs(object? e, Exception ex)
        {
            NotificationManager.Instance.Notify($"插件运行异常 ({e?.GetType().Module.Name})：{ex.Message}",
                "插件异常", NotificationType.Error);
            Stop();
        }


        private void TimerCallback(object? state)
        {
            RunningSeconds++;
            OnUpdateRunningSeconds(RunningSeconds);
        }

        private void StopRecognize()
        {
            try
            {
                _audioSource?.Stop();
                _recognizer?.Stop();
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"停止失败：{ex.Message}", "停止失败", NotificationType.Fatal);
                // TODO: exit or recover ?
                return;
            }

            _audioSource.DataAvailable -= OnAudioSourceOnDataAvailable;
            _audioSource.ExceptionOccured -= OnPluginRunningExceptionOccurs;

            _recognizer.TextChanged -= OnRecognizerOnTextChanged;
            _recognizer.SentenceDone -= OnRecognizerOnSentenceDone;
            _recognizer.ExceptionOccured -= OnPluginRunningExceptionOccurs;


            _audioSource = null;
            _recognizer = null;
        }

        public override void Start()
        {
            if (Status == JobStatus.Running) return;
            StartRecognize();
        }

        public override void Pause()
        {
            if (Status == JobStatus.Running) StopRecognize();
            Status = JobStatus.Paused;

            _timer?.Dispose();
            _timer = null;
        }

        public override void Stop()
        {
            if (Status == JobStatus.Running) StopRecognize();
            Status = JobStatus.Stopped;

            _timer?.Dispose();
            _timer = null;
        }
    }
}
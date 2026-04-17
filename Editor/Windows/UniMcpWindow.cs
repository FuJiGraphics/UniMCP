using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UniMCP.Editor.Chat;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.Windows
{
    public class UniMcpWindow : EditorWindow
    {
        private static int _tabCounter;

        [SerializeField] private List<ChatMessage> _messages = new();
        [SerializeField] private string _input = "";
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private string _claudeSessionId;
        [SerializeField] private int _tabNumber;

        private bool _isWaiting;
        private bool _scrollToBottom;
        private double _thinkingStartedAt;
        private int _thinkingDots;

        [MenuItem("UniMCP/New Chat")]
        private static void OpenNewChat()
        {
            var window = CreateWindow<UniMcpWindow>();
            window.AssignTabNumber();
            window.minSize = new Vector2(420, 360);
            window.Show();
        }

        private void OnEnable()
        {
            if (_tabNumber == 0)
                AssignTabNumber();
            ApplyTitle();

            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void AssignTabNumber()
        {
            _tabCounter++;
            _tabNumber = _tabCounter;
            ApplyTitle();
        }

        private void ApplyTitle()
        {
            titleContent = new GUIContent($"UniMCP #{_tabNumber}");
        }

        private void OnEditorUpdate()
        {
            if (!_isWaiting)
                return;

            var elapsed = EditorApplication.timeSinceStartup - _thinkingStartedAt;
            var newDots = ((int)(elapsed * 2)) % 4;
            if (newDots != _thinkingDots)
            {
                _thinkingDots = newDots;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawChat();
        }

        private void DrawChat()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_messages.Count == 0 && !_isWaiting)
            {
                EditorGUILayout.Space(40);
                var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };
                EditorGUILayout.LabelField(
                    "Start chatting with Claude. First run of `claude` CLI login is required.",
                    hint);
                EditorGUILayout.LabelField(
                    "Open another session via UniMCP → New Chat.",
                    hint);
            }
            else
            {
                foreach (var m in _messages)
                    DrawMessage(m);

                if (_isWaiting)
                    DrawThinking();
            }

            if (_scrollToBottom && Event.current.type == EventType.Repaint)
            {
                _scroll.y = float.MaxValue;
                _scrollToBottom = false;
            }

            EditorGUILayout.EndScrollView();

            DrawInputBar();
        }

        private void DrawMessage(ChatMessage m)
        {
            var (header, color) = m.role switch
            {
                eChatRole.User      => ("You",    new Color(0.40f, 0.80f, 1.00f)),
                eChatRole.Assistant => ("Claude", new Color(0.70f, 1.00f, 0.70f)),
                _                   => ("System", new Color(1.00f, 0.60f, 0.40f)),
            };

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var headerStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    normal = { textColor = color },
                };
                EditorGUILayout.LabelField(header, headerStyle);

                var bodyStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = true,
                };
                var rendered = m.role == eChatRole.Assistant
                    ? MarkdownRenderer.ToRichText(m.text)
                    : m.text;
                EditorGUILayout.LabelField(rendered, bodyStyle);
            }

            EditorGUILayout.Space(2);
        }

        private void DrawThinking()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var headerStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    normal = { textColor = new Color(0.70f, 1.00f, 0.70f) },
                };
                EditorGUILayout.LabelField("Claude", headerStyle);

                var body = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.70f, 0.70f, 0.70f) },
                };
                var dots = new string('.', _thinkingDots);
                EditorGUILayout.LabelField($"Thinking{dots}", body);
            }

            EditorGUILayout.Space(2);
        }

        private void DrawInputBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_isWaiting;
                _input = EditorGUILayout.TextArea(_input, GUILayout.MinHeight(44));

                var label = _isWaiting ? "..." : "Send";
                if (GUILayout.Button(label, GUILayout.Width(72), GUILayout.Height(44)))
                    _ = SendAsync();
                GUI.enabled = true;
            }
        }

        private async Task SendAsync()
        {
            var text = _input.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            _messages.Add(new ChatMessage { role = eChatRole.User, text = text });
            _input = "";
            _isWaiting = true;
            _thinkingStartedAt = EditorApplication.timeSinceStartup;
            _thinkingDots = 0;
            _scrollToBottom = true;
            Repaint();

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var response = await ClaudeProcess.Send(text, projectRoot, _claudeSessionId);

                if (!string.IsNullOrEmpty(response.session_id))
                    _claudeSessionId = response.session_id;

                _messages.Add(new ChatMessage
                {
                    role = eChatRole.Assistant,
                    text = response.result ?? "",
                });
            }
            catch (Exception e)
            {
                _messages.Add(new ChatMessage { role = eChatRole.System, text = e.Message });
            }
            finally
            {
                _isWaiting = false;
                _scrollToBottom = true;
                Repaint();
            }
        }
    }
}

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
        private const bool ShowSessionTabs = false;

        [SerializeField] private List<ChatSessionData> _sessions = new();
        [SerializeField] private int _activeIdx;

        [MenuItem("UniMCP/Open Window")]
        private static void Open()
        {
            var window = GetWindow<UniMcpWindow>("UniMCP");
            window.minSize = new Vector2(460, 400);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureAtLeastOneSession();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void EnsureAtLeastOneSession()
        {
            if (_sessions.Count == 0)
                _sessions.Add(NewSession());

            if (_activeIdx < 0 || _activeIdx >= _sessions.Count)
                _activeIdx = 0;
        }

        private ChatSessionData NewSession()
        {
            return new ChatSessionData { name = $"Session {_sessions.Count + 1}" };
        }

        private ChatSessionData Active => _sessions[_activeIdx];

        private void OnEditorUpdate()
        {
            var s = Active;
            if (!s.isWaiting)
                return;

            var elapsed = EditorApplication.timeSinceStartup - s.thinkingStartedAt;
            var newDots = ((int)(elapsed * 2)) % 4;
            if (newDots != s.thinkingDots)
            {
                s.thinkingDots = newDots;
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (ShowSessionTabs)
            {
                DrawSessionTabs();
                EditorGUILayout.Space(4);
            }
            DrawChat(Active);
        }

        private void DrawSessionTabs()
        {
            int pendingClose = -1;
            bool addClicked = false;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    var selected = i == _activeIdx;
                    var style = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                        alignment = TextAnchor.MiddleLeft,
                        padding = new RectOffset(8, 4, 0, 0),
                    };

                    if (GUILayout.Button(_sessions[i].name, style, GUILayout.MinWidth(80)))
                        _activeIdx = i;

                    GUI.enabled = _sessions.Count > 1;
                    if (GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(20)))
                        pendingClose = i;
                    GUI.enabled = true;
                }

                if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24)))
                    addClicked = true;

                GUILayout.FlexibleSpace();
            }

            if (pendingClose >= 0)
                CloseSession(pendingClose);

            if (addClicked)
                AddSession();
        }

        private void AddSession()
        {
            _sessions.Add(NewSession());
            _activeIdx = _sessions.Count - 1;
            Repaint();
        }

        private void CloseSession(int index)
        {
            if (_sessions.Count <= 1)
                return;

            _sessions.RemoveAt(index);
            if (_activeIdx >= _sessions.Count)
                _activeIdx = _sessions.Count - 1;
            else if (index < _activeIdx)
                _activeIdx--;

            Repaint();
        }

        private void DrawChat(ChatSessionData s)
        {
            s.scroll = EditorGUILayout.BeginScrollView(s.scroll);

            if (s.messages.Count == 0 && !s.isWaiting)
            {
                EditorGUILayout.Space(40);
                var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };
                EditorGUILayout.LabelField(
                    "Start chatting with Claude. First run of `claude` CLI login is required.",
                    hint);
            }
            else
            {
                foreach (var m in s.messages)
                    DrawMessage(m);

                if (s.isWaiting)
                    DrawThinking(s);
            }

            if (s.scrollToBottom && Event.current.type == EventType.Repaint)
            {
                s.scroll.y = float.MaxValue;
                s.scrollToBottom = false;
            }

            EditorGUILayout.EndScrollView();

            DrawInputBar(s);
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

        private void DrawThinking(ChatSessionData s)
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
                var dots = new string('.', s.thinkingDots);
                EditorGUILayout.LabelField($"Thinking{dots}", body);
            }

            EditorGUILayout.Space(2);
        }

        private void DrawInputBar(ChatSessionData s)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !s.isWaiting;
                s.input = EditorGUILayout.TextArea(s.input, GUILayout.MinHeight(44));

                var label = s.isWaiting ? "..." : "Send";
                if (GUILayout.Button(label, GUILayout.Width(72), GUILayout.Height(44)))
                    _ = SendAsync(s);
                GUI.enabled = true;
            }
        }

        private async Task SendAsync(ChatSessionData s)
        {
            var text = s.input.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            s.messages.Add(new ChatMessage { role = eChatRole.User, text = text });
            s.input = "";
            s.isWaiting = true;
            s.thinkingStartedAt = EditorApplication.timeSinceStartup;
            s.thinkingDots = 0;
            s.scrollToBottom = true;
            Repaint();

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var response = await ClaudeProcess.Send(text, projectRoot, s.claudeSessionId);

                if (!string.IsNullOrEmpty(response.session_id))
                    s.claudeSessionId = response.session_id;

                s.messages.Add(new ChatMessage
                {
                    role = eChatRole.Assistant,
                    text = response.result ?? "",
                });
            }
            catch (Exception e)
            {
                s.messages.Add(new ChatMessage { role = eChatRole.System, text = e.Message });
            }
            finally
            {
                s.isWaiting = false;
                s.scrollToBottom = true;
                Repaint();
            }
        }
    }
}

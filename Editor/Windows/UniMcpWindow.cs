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
        private enum eTab
        {
            Chat = 0,
            Status = 1,
        }

        private eTab _currentTab = eTab.Chat;
        private readonly List<ChatMessage> _messages = new();
        private string _input = "";
        private Vector2 _scrollMessages;
        private bool _isWaiting;
        private bool _scrollToBottom;

        [MenuItem("UniMCP/Open Window")]
        private static void Open()
        {
            var window = GetWindow<UniMcpWindow>("UniMCP");
            window.minSize = new Vector2(480, 420);
            window.Show();
        }

        private void OnGUI()
        {
            DrawTabs();
            EditorGUILayout.Space(4);

            switch (_currentTab)
            {
                case eTab.Chat:   DrawChat(); break;
                case eTab.Status: DrawStatus(); break;
            }
        }

        private void DrawTabs()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                DrawTabButton("💬 Chat", eTab.Chat);
                DrawTabButton("🔌 Status", eTab.Status);
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawTabButton(string label, eTab tab)
        {
            var selected = _currentTab == tab;
            var style = new GUIStyle(EditorStyles.toolbarButton)
            {
                fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
            };
            if (GUILayout.Toggle(selected, label, style) && !selected)
                _currentTab = tab;
        }

        private void DrawChat()
        {
            _scrollMessages = EditorGUILayout.BeginScrollView(_scrollMessages);

            if (_messages.Count == 0)
            {
                EditorGUILayout.Space(40);
                var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };
                EditorGUILayout.LabelField(
                    "Start chatting with Claude. First run of `claude` CLI login is required.",
                    hint);
            }
            else
            {
                foreach (var m in _messages)
                    DrawMessage(m);
            }

            if (_scrollToBottom && Event.current.type == EventType.Repaint)
            {
                _scrollMessages.y = float.MaxValue;
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
                    richText = false,
                };
                EditorGUILayout.LabelField(m.text, bodyStyle);
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

            _messages.Add(new ChatMessage
            {
                role = eChatRole.User,
                text = text,
                timestamp = DateTime.Now,
            });
            _input = "";
            _isWaiting = true;
            _scrollToBottom = true;
            Repaint();

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                var response = await ClaudeProcess.Send(text, projectRoot);
                _messages.Add(new ChatMessage
                {
                    role = eChatRole.Assistant,
                    text = response,
                    timestamp = DateTime.Now,
                });
            }
            catch (Exception e)
            {
                _messages.Add(new ChatMessage
                {
                    role = eChatRole.System,
                    text = e.Message,
                    timestamp = DateTime.Now,
                });
            }
            finally
            {
                _isWaiting = false;
                _scrollToBottom = true;
                Repaint();
            }
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField("Bridge Status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "HTTP bridge server is not yet implemented. Phase 1 will expose status, port, and recent tool-call logs here.",
                MessageType.Info);
        }
    }
}

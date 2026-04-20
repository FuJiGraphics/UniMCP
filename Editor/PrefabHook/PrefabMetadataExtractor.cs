// JsonUtility 가 채우는 필드는 컴파일러가 미할당으로 오진
#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniMCP.Editor.PrefabHook
{
    /// <summary>
    /// Reference 프리팹의 구조·컴포넌트·추정 타입을 읽어 Analyzer 가 의미적으로 매칭할 수 있게 제공.
    /// AssetDatabase 로 프리팹 asset 을 로드해 Editor-safe 한 introspection 수행
    /// </summary>
    public static class PrefabMetadataExtractor
    {
        [Serializable]
        public class PrefabMeta
        {
            public string path;
            public string rootName;
            public string[] rootComponents;
            public ChildMeta[] children;
            public string kind;               // 추정 타입: button / toggle / cell / layout / image / text / scroll / generic
            public int approximateWidth;
            public int approximateHeight;
        }

        [Serializable]
        public class ChildMeta
        {
            public string name;
            public string[] components;
        }

        public static PrefabMeta Extract(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath)) return null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (go == null) return null;

            var meta = new PrefabMeta
            {
                path = prefabPath,
                rootName = go.name,
                rootComponents = GetComponentNames(go).ToArray(),
            };

            var kids = new List<ChildMeta>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var c = go.transform.GetChild(i);
                kids.Add(new ChildMeta
                {
                    name = c.gameObject.name,
                    components = GetComponentNames(c.gameObject).ToArray(),
                });
            }
            meta.children = kids.ToArray();

            if (go.TryGetComponent<RectTransform>(out var rt))
            {
                meta.approximateWidth = (int)rt.sizeDelta.x;
                meta.approximateHeight = (int)rt.sizeDelta.y;
            }

            meta.kind = InferKind(meta);
            return meta;
        }

        public static PrefabMeta[] ExtractAll(IEnumerable<string> paths)
        {
            var list = new List<PrefabMeta>();
            if (paths == null) return list.ToArray();
            foreach (var p in paths)
            {
                var m = Extract(p);
                if (m != null) list.Add(m);
            }
            return list.ToArray();
        }

        private static string InferKind(PrefabMeta m)
        {
            var rn = (m.rootName ?? "").ToLowerInvariant();
            var comps = m.rootComponents ?? Array.Empty<string>();

            if (Array.Exists(comps, c => c == "Button"))               return "button";
            if (Array.Exists(comps, c => c == "Toggle"))               return "toggle";
            if (Array.Exists(comps, c => c == "ScrollRect"))           return "scroll";
            if (Array.Exists(comps, c => c == "Slider"))               return "slider";
            if (Array.Exists(comps, c => c == "Dropdown" || c == "TMP_Dropdown")) return "dropdown";
            if (Array.Exists(comps, c => c == "InputField" || c == "TMP_InputField")) return "input";
            if (rn.StartsWith("cell") || rn.Contains("cell"))          return "cell";
            if (Array.Exists(comps, c => c.EndsWith("LayoutGroup")))   return "layout";
            if (Array.Exists(comps, c => c == "Image" || c == "RawImage")) return "image";
            if (Array.Exists(comps, c => c == "TextMeshProUGUI" || c == "Text")) return "text";
            return "generic";
        }

        private static IEnumerable<string> GetComponentNames(GameObject go)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                yield return c.GetType().Name;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace com.yoozoo.ball.Editor.AssetTools
{
    // ===================================================================================
    // 1. 数据结构 (Data Models)
    // ===================================================================================

    [Serializable]
    public class RefNodeData
    {
        public string id;           // GUID
        public string path;         // Asset Path
        public string typeName;     // Type Name
        public float x;             // Graph Position X
        public float y;             // Graph Position Y
        public int level;           // Depth Level
        
        // [New Feature] 📦 文件大小
        public long fileSize;       // File Size in Bytes
    }

    [Serializable]
    public class RefEdgeData
    {
        public string sourceId;
        public string targetId;
    }

    [Serializable]
    public class RefSnapshot
    {
        public string snapshotName;
        public string timestamp;
        public string rootAssetGuid;
        public List<RefNodeData> nodes = new List<RefNodeData>();
        public List<RefEdgeData> edges = new List<RefEdgeData>();
    }

    [Serializable]
    public class CacheEntry
    {
        public string id;
        public List<string> list;
    }

    [Serializable]
    public class CacheData
    {
        public List<CacheEntry> reverseEntries = new List<CacheEntry>();
        public List<CacheEntry> forwardEntries = new List<CacheEntry>(); 
    }

    // ===================================================================================
    // 2. 持久化缓存与增量更新 (Persistence & Incremental Cache)
    // ===================================================================================

    public class DependencyCache
    {
        private Dictionary<string, HashSet<string>> _reverseMap = new Dictionary<string, HashSet<string>>();
        private Dictionary<string, HashSet<string>> _forwardMap = new Dictionary<string, HashSet<string>>();
        
        private bool _isDirty = false;
        private const string CACHE_FILE_PATH = "Library/RefAnalyzerCache.json";

        public bool HasIndex => _reverseMap.Count > 0;

        public void Initialize()
        {
            LoadFromDisk();
        }

        public void SaveToDisk()
        {
            if (!_isDirty) return;

            var data = new CacheData();
            
            foreach (var kvp in _reverseMap)
            {
                if (kvp.Value.Count > 0)
                    data.reverseEntries.Add(new CacheEntry { id = kvp.Key, list = kvp.Value.ToList() });
            }

            foreach (var kvp in _forwardMap)
            {
                 if (kvp.Value.Count > 0)
                    data.forwardEntries.Add(new CacheEntry { id = kvp.Key, list = kvp.Value.ToList() });
            }

            try
            {
                string json = JsonUtility.ToJson(data, false);
                File.WriteAllText(CACHE_FILE_PATH, json);
                _isDirty = false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[RefViewer] Failed to save cache: {e.Message}");
            }
        }

        private void LoadFromDisk()
        {
            if (!File.Exists(CACHE_FILE_PATH)) return;

            try
            {
                string json = File.ReadAllText(CACHE_FILE_PATH);
                var data = JsonUtility.FromJson<CacheData>(json);

                _reverseMap.Clear();
                _forwardMap.Clear();

                foreach (var entry in data.reverseEntries)
                    _reverseMap[entry.id] = new HashSet<string>(entry.list);

                foreach (var entry in data.forwardEntries)
                    _forwardMap[entry.id] = new HashSet<string>(entry.list);
            }
            catch
            {
                _reverseMap.Clear();
                _forwardMap.Clear();
            }
        }

        public void BuildIndexFull()
        {
            _reverseMap.Clear();
            _forwardMap.Clear();
            
            string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
            int count = 0;
            int total = allAssetPaths.Length;

            try 
            {
                foreach (var path in allAssetPaths)
                {
                    if (ShouldIgnore(path)) continue;

                    if (count % 100 == 0)
                        EditorUtility.DisplayProgressBar("正在构建索引", $"扫描中: {path}", (float)count / total);
                    count++;

                    UpdateSingleAssetIndex(path);
                }
            }
            finally
            {
                _isDirty = true;
                SaveToDisk();
                EditorUtility.ClearProgressBar();
                Debug.Log($"[RefViewer] Index Built. Tracked {count} assets.");
            }
        }

        public void UpdateAsset(string assetPath)
        {
            if (ShouldIgnore(assetPath)) return;
            UpdateSingleAssetIndex(assetPath);
            _isDirty = true;
        }

        public void RemoveAsset(string assetPath)
        {
            // 简化处理：删除不更新缓存，等待Rebuild或下次Import校正
        }

        private void UpdateSingleAssetIndex(string path)
        {
            string sourceGuid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(sourceGuid)) return;

            string[] currentDeps = AssetDatabase.GetDependencies(path, false);
            HashSet<string> newDepGuids = new HashSet<string>();

            foreach (var depPath in currentDeps)
            {
                if (depPath == path) continue;
                string dGuid = AssetDatabase.AssetPathToGUID(depPath);
                if (!string.IsNullOrEmpty(dGuid)) newDepGuids.Add(dGuid);
            }

            HashSet<string> oldDepGuids = null;
            if (_forwardMap.ContainsKey(sourceGuid))
            {
                oldDepGuids = _forwardMap[sourceGuid];
            }

            if (oldDepGuids != null)
            {
                foreach (var oldG in oldDepGuids)
                {
                    if (!newDepGuids.Contains(oldG))
                    {
                        if (_reverseMap.ContainsKey(oldG))
                        {
                            _reverseMap[oldG].Remove(sourceGuid);
                            if (_reverseMap[oldG].Count == 0) _reverseMap.Remove(oldG);
                        }
                    }
                }
            }

            foreach (var newG in newDepGuids)
            {
                if (!_reverseMap.ContainsKey(newG)) _reverseMap[newG] = new HashSet<string>();
                _reverseMap[newG].Add(sourceGuid);
            }

            _forwardMap[sourceGuid] = newDepGuids;
        }

        private bool ShouldIgnore(string path)
        {
            // [Fix] 忽略隐藏文件或特殊文件夹，但保留Assets下内容
            return string.IsNullOrEmpty(path) || path.StartsWith("Packages/") || path.EndsWith(".unitypackage");
        }

        public List<string> GetReferencers(string targetGuid)
        {
            if (_reverseMap.TryGetValue(targetGuid, out var list))
                return list.ToList();
            return new List<string>();
        }
        
        // [New Feature] 🏝️ 获取所有被索引但零引用的资源
        public List<string> GetOrphanCandidates(string folderPath)
        {
            var results = new List<string>();
            string[] guids = AssetDatabase.FindAssets("", new[] { folderPath });
            
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if(ShouldIgnore(path) || Directory.Exists(path)) continue; // Skip folders

                // 如果在反向索引里没有Key，或者Key对应的List为空，则视为无引用
                if (!_reverseMap.ContainsKey(guid) || _reverseMap[guid].Count == 0)
                {
                    results.Add(guid);
                }
            }
            return results;
        }
    }

    public class RefAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!RefAnalyzer.Cache.HasIndex) return;

            bool changed = false;
            foreach (var path in importedAssets) { RefAnalyzer.Cache.UpdateAsset(path); changed = true; }
            foreach (var path in movedAssets) { RefAnalyzer.Cache.UpdateAsset(path); changed = true; }

            if (changed) RefAnalyzer.Cache.SaveToDisk();
        }
    }

    // ===================================================================================
    // 3. 逻辑分析层 (Analyzer)
    // ===================================================================================

    [InitializeOnLoad]
    public static class RefAnalyzer
    {
        public static DependencyCache Cache = new DependencyCache();

        static RefAnalyzer()
        {
            Cache.Initialize();
        }

        public static RefSnapshot Analyze(string rootGuid, int maxDepth = 1, bool ignoreScripts = false)
        {
            if (string.IsNullOrEmpty(rootGuid)) return null;

            var snapshot = new RefSnapshot
            {
                snapshotName = $"Ref_Analysis_{DateTime.Now:HH_mm_ss}",
                timestamp = DateTime.Now.ToString(),
                rootAssetGuid = rootGuid
            };

            var processedGuids = new HashSet<string>();
            var queue = new Queue<(string guid, int level)>();
            
            queue.Enqueue((rootGuid, 0));
            processedGuids.Add(rootGuid);

            var tempEdges = new List<(string src, string dst)>();

            while (queue.Count > 0)
            {
                var (currentGuid, level) = queue.Dequeue();

                string path = AssetDatabase.GUIDToAssetPath(currentGuid);
                if (string.IsNullOrEmpty(path)) continue;

                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                string typeName = assetType != null ? assetType.Name : "Unknown";

                if (ignoreScripts && (typeName == "MonoScript" || typeName == "Shader"))
                {
                    if (level != 0) continue; 
                }

                // [New Feature] 📦 获取文件大小
                long size = 0;
                try {
                    if (File.Exists(path)) size = new FileInfo(path).Length;
                } catch {}

                snapshot.nodes.Add(new RefNodeData
                {
                    id = currentGuid,
                    path = path,
                    typeName = typeName,
                    level = level,
                    fileSize = size // Set size
                });

                if (Mathf.Abs(level) >= maxDepth) continue;

                if (level >= 0)
                {
                    string[] deps = AssetDatabase.GetDependencies(path, false);
                    foreach (var depPath in deps)
                    {
                        if (depPath == path) continue;
                        string depGuid = AssetDatabase.AssetPathToGUID(depPath);
                        tempEdges.Add((currentGuid, depGuid));

                        if (!processedGuids.Contains(depGuid))
                        {
                            processedGuids.Add(depGuid);
                            queue.Enqueue((depGuid, level + 1));
                        }
                    }
                }

                if (level <= 0)
                {
                    List<string> referencers = Cache.GetReferencers(currentGuid);
                    foreach (var refGuid in referencers)
                    {
                        tempEdges.Add((refGuid, currentGuid));

                        if (!processedGuids.Contains(refGuid))
                        {
                            processedGuids.Add(refGuid);
                            queue.Enqueue((refGuid, level - 1));
                        }
                    }
                }
            }

            var validNodeIds = new HashSet<string>(snapshot.nodes.Select(n => n.id));
            foreach (var edge in tempEdges)
            {
                if (validNodeIds.Contains(edge.src) && validNodeIds.Contains(edge.dst))
                {
                    snapshot.edges.Add(new RefEdgeData { sourceId = edge.src, targetId = edge.dst });
                }
            }

            return snapshot;
        }
    }

    // ===================================================================================
    // 4. UI 表现层 (GraphView & Nodes)
    // ===================================================================================

    public class AssetNode : Node
    {
        public string Guid;
        public string AssetPath;
        public int Level;
        public string TypeName;
        public long FileSize;

        private Label _sizeLabel;
        private VisualElement _titleContainer;

        public AssetNode(RefNodeData data)
        {
            this.Guid = data.id;
            this.AssetPath = data.path;
            this.Level = data.level;
            this.TypeName = data.typeName;
            this.FileSize = data.fileSize;
            
            this.title = Path.GetFileNameWithoutExtension(data.path);
            this.tooltip = $"{data.path}\nSize: {FormatSize(data.fileSize)}";

            _titleContainer = titleContainer; // Cache referencing

            var icon = new Image();
            icon.style.width = 40;
            icon.style.height = 40;
            icon.style.alignSelf = Align.Center;
            icon.scaleMode = ScaleMode.ScaleToFit;
            
            Texture2D preview = AssetPreview.GetAssetPreview(AssetDatabase.LoadAssetAtPath<Object>(data.path));
            if (preview == null) 
                preview = AssetPreview.GetMiniThumbnail(AssetDatabase.LoadAssetAtPath<Object>(data.path));
            
            icon.image = preview;
            extensionContainer.Add(icon);

            var typeLabel = new Label(data.typeName);
            typeLabel.style.fontSize = 10;
            typeLabel.style.color = new StyleColor(Color.gray);
            typeLabel.style.alignSelf = Align.Center;
            extensionContainer.Add(typeLabel);

            // [New Feature] 📦 Size Label (Hidden by default)
            _sizeLabel = new Label(FormatSize(data.fileSize));
            _sizeLabel.style.fontSize = 10;
            _sizeLabel.style.alignSelf = Align.Center;
            _sizeLabel.style.color = new StyleColor(new Color(0.9f, 0.9f, 0.9f));
            _sizeLabel.style.display = DisplayStyle.None; // Hide initially
            extensionContainer.Add(_sizeLabel);

            // Default Color Logic
            SetDefaultColor();

            // Special highlight for Root
            if (data.level == 0)
            {
                var borderColor = new StyleColor(Color.yellow);
                this.style.borderTopColor = borderColor;
                this.style.borderBottomColor = borderColor;
                this.style.borderLeftColor = borderColor;
                this.style.borderRightColor = borderColor;
                this.style.borderBottomWidth = 2;
                this.style.borderTopWidth = 2;
                this.style.borderLeftWidth = 2;
                this.style.borderRightWidth = 2;
            }

            int referencerCount = RefAnalyzer.Cache.GetReferencers(data.id).Count;
            int dependencyCount = AssetDatabase.GetDependencies(data.path, false).Count(d => d != data.path);

            var inPort = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            inPort.portName = $"被引 ({referencerCount})";
            inputContainer.Add(inPort);

            var outPort = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            outPort.portName = $"引用 ({dependencyCount})";
            outputContainer.Add(outPort);

            this.expanded = true;
            RefreshExpandedState();
        }

        // [New Feature] 📦 热力图逻辑
        public void SetHeatmapMode(bool active)
        {
            if (active)
            {
                // Show Size Text
                _sizeLabel.style.display = DisplayStyle.Flex;
                
                // Color Gradient
                // Green < 500KB
                // Yellow < 2MB
                // Red >= 2MB
                Color c;
                if (FileSize < 500 * 1024) c = new Color(0.2f, 0.6f, 0.2f); // Green
                else if (FileSize < 2 * 1024 * 1024) c = new Color(0.8f, 0.7f, 0.1f); // Yellow
                else c = new Color(0.8f, 0.2f, 0.2f); // Red

                _titleContainer.style.backgroundColor = new StyleColor(c);
            }
            else
            {
                // Revert
                _sizeLabel.style.display = DisplayStyle.None;
                SetDefaultColor();
            }
        }

        private void SetDefaultColor()
        {
            Color titleColor = new Color(0.2f, 0.2f, 0.2f);
            if (Level == 0) titleColor = new Color(0.6f, 0.4f, 0.0f);
            else if (TypeName == "Material") titleColor = new Color(0.2f, 0.4f, 0.2f);
            else if (TypeName == "GameObject") titleColor = new Color(0.2f, 0.2f, 0.5f);
            else if (TypeName == "Texture2D") titleColor = new Color(0.5f, 0.2f, 0.2f);
            
            _titleContainer.style.backgroundColor = new StyleColor(titleColor);
        }

        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0f).ToString("F1") + " KB";
            return (bytes / (1024.0f * 1024.0f)).ToString("F2") + " MB";
        }
    }

    // ===================================================================================
    // 4. [优化版] UI 表现层 (GraphView & Nodes) - 支持自动分组
    // ===================================================================================
    // ===================================================================================
    // 4. [优化版] UI 表现层 (GraphView & Nodes) - 搜索仅变暗，不改变布局
    // ===================================================================================
    public class ReferenceGraphView : GraphView
    {
        public ReferenceViewerWindow Window;
        private MiniMap _miniMap;

        public ReferenceGraphView(ReferenceViewerWindow window)
        {
            this.Window = window;

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
            this.style.flexGrow = 1;

            _miniMap = new MiniMap { anchored = true };
            _miniMap.style.width = 200;
            _miniMap.style.height = 100;
            _miniMap.style.position = Position.Absolute;
            _miniMap.style.right = 10;
            _miniMap.style.bottom = 10;
            _miniMap.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f, 0.8f));
            Add(_miniMap);

            RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                Window.NavigateBack();
                evt.StopPropagation();
            }
        }
        
        // --- Selection Logic (保持原有的高亮逻辑) ---
        public override void AddToSelection(ISelectable selectable) 
        { 
            base.AddToSelection(selectable); 
            schedule.Execute(OnSelectionChanged).ExecuteLater(0); 
        }
        
        public override void RemoveFromSelection(ISelectable selectable) 
        { 
            base.RemoveFromSelection(selectable); 
            schedule.Execute(OnSelectionChanged).ExecuteLater(0); 
        }
        
        public override void ClearSelection() 
        { 
            base.ClearSelection(); 
            OnSelectionChanged(); 
        }

        private void OnSelectionChanged() 
        { 
            // 1. 同步 Project 窗口选择
            if (Window.SyncSelection && selection.Count > 0) 
            { 
                var assetNodes = selection.OfType<AssetNode>().Select(n => AssetDatabase.LoadAssetAtPath<Object>(n.AssetPath)).ToArray(); 
                if (assetNodes.Length > 0) Selection.objects = assetNodes; 
            } 
            
            // 2. 如果当前有搜索词，优先保留搜索的高亮状态，或者清除搜索？
            // 通常逻辑：点击选择会覆盖搜索的高亮效果
            HighlightConnections(); 
        }

        private void HighlightConnections() 
        { 
            // 如果没有选择，恢复全部亮度（除非有搜索过滤，这里简单起见恢复全部）
            if (selection.Count == 0)
            {
                // 如果搜索框有字，这里可能会重置掉搜索结果。
                // 为了体验更好，我们在 Window 端调用 ApplyFilter() 来重新应用搜索状态
                Window.ApplyFilter(); 
                return;
            }

            var sn = selection.OfType<AssetNode>().ToList(); 
            if (sn.Count == 0) return; 

            var relatedEdges = new HashSet<Edge>(); 
            var relatedNodes = new HashSet<Node>(); 
            
            foreach(var n in sn)
            {
                relatedNodes.Add(n); 
                CollectUpstream(n, relatedEdges, relatedNodes); 
                CollectDownstream(n, relatedEdges, relatedNodes);
            } 

            foreach(var el in graphElements.ToList())
            { 
                if(el is Node n) 
                    n.style.opacity = relatedNodes.Contains(n) ? 1f : 0.1f; 
                else if(el is Edge e)
                { 
                    if(relatedEdges.Contains(e))
                    {
                        e.style.opacity = 1f; 
                        e.BringToFront();
                    } 
                    else 
                        e.style.opacity = 0.05f;
                }
            }
        }

        private void CollectUpstream(Node n, HashSet<Edge> e, HashSet<Node> ns) 
        { 
            if(n.inputContainer.Q<Port>() is Port p && p.connected) 
                foreach(Edge ed in p.connections) 
                    if(!e.Contains(ed))
                    { 
                        e.Add(ed); 
                        if(ed.output.node!=null && !ns.Contains(ed.output.node))
                        { 
                            ns.Add(ed.output.node); 
                            CollectUpstream(ed.output.node,e,ns);
                        }
                    }
        }

        private void CollectDownstream(Node n, HashSet<Edge> e, HashSet<Node> ns) 
        { 
            if(n.outputContainer.Q<Port>() is Port p && p.connected) 
                foreach(Edge ed in p.connections) 
                    if(!e.Contains(ed))
                    { 
                        e.Add(ed); 
                        if(ed.input.node!=null && !ns.Contains(ed.input.node))
                        { 
                            ns.Add(ed.input.node); 
                            CollectDownstream(ed.input.node,e,ns);
                        }
                    }
        }

        // --- Search Logic (修改的核心部分) ---
        public void FilterNodes(string searchText)
        {
            // 如果当前有选中物体，且搜索框为空，则优先保持 Select 的高亮逻辑
            if (string.IsNullOrEmpty(searchText) && selection.Count > 0)
            {
                HighlightConnections();
                return;
            }

            searchText = searchText?.ToLower() ?? "";
            bool isEmpty = string.IsNullOrEmpty(searchText);

            // 1. 找出所有匹配的 Node
            var matchingNodes = new HashSet<Node>();
            if (!isEmpty)
            {
                foreach (var node in nodes.ToList().OfType<AssetNode>())
                {
                    if (node.title.ToLower().Contains(searchText))
                        matchingNodes.Add(node);
                }
            }

            // 2. 应用视觉效果 (Opacity)
            foreach (var elem in graphElements.ToList())
            {
                if (elem is AssetNode node)
                {
                    // 关键修改：始终保持 Flex，不改变布局位置，仅改变透明度
                    node.style.display = DisplayStyle.Flex; 

                    if (isEmpty)
                    {
                        node.style.opacity = 1f;
                    }
                    else
                    {
                        bool isMatch = matchingNodes.Contains(node);
                        node.style.opacity = isMatch ? 1f : 0.1f; // 未匹配的变暗
                    }
                }
                else if (elem is Edge edge)
                {
                    if (isEmpty)
                    {
                        edge.style.opacity = 1f;
                    }
                    else
                    {
                        // 逻辑优化：如果边的任意一端连接到了“搜索结果”，则保持边的可见性，方便查看上下文
                        bool sourceMatch = edge.output.node != null && matchingNodes.Contains(edge.output.node);
                        bool targetMatch = edge.input.node != null && matchingNodes.Contains(edge.input.node);
                        
                        edge.style.opacity = (sourceMatch || targetMatch) ? 1f : 0.05f;
                    }
                }
                // Group 保持可见，不处理透明度，或者也可以随之变暗，这里保持 Group 框可见作为结构参考
            }
        }

        public void ToggleHeatmap(bool active)
        {
            foreach (var elem in graphElements.ToList())
            {
                if (elem is AssetNode node) node.SetHeatmapMode(active);
            }
        }

        public void RebuildGraph(RefSnapshot snapshot)
        {
            DeleteElements(graphElements);
            if (!this.Contains(_miniMap)) Add(_miniMap);

            if (snapshot == null) return;

            var nodeMap = new Dictionary<string, AssetNode>();
            var nodesByLevel = new Dictionary<int, List<AssetNode>>();

            // 1. 创建节点
            foreach (var nodeData in snapshot.nodes)
            {
                var node = new AssetNode(nodeData);
                nodeMap[nodeData.id] = node;

                if (!nodesByLevel.ContainsKey(nodeData.level)) nodesByLevel[nodeData.level] = new List<AssetNode>();
                nodesByLevel[nodeData.level].Add(node);

                node.RegisterCallback<MouseDownEvent>(evt => { if (evt.clickCount == 2) Window.NavigateTo(nodeData.id); });
                node.AddManipulator(new ContextualMenuManipulator(evt => {
                    evt.menu.AppendAction("在项目中定位", a => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(nodeData.path)));
                    evt.menu.AppendAction("替换引用...", a => ReferenceReplacementWindow.Open(nodeData.id));
                }));
            }

            // 2. 布局 (Group by Type)
            float colWidth = 400f;  
            float nodeHeight = 150f; 
            float groupHeaderHeight = 40f; 
            float groupPadding = 20f;

            foreach (var level in nodesByLevel.Keys.OrderBy(k => k))
            {
                var nodesInLevel = nodesByLevel[level];
                
                var nodesByType = nodesInLevel
                    .GroupBy(n => n.TypeName)
                    .OrderBy(g => g.Key) 
                    .ToList();

                float currentY = 0f;

                foreach (var typeGroup in nodesByType)
                {
                    string typeName = typeGroup.Key;
                    var groupNodes = typeGroup.ToList();

                    var group = new Group();
                    group.title = $"{typeName} ({groupNodes.Count})";
                    group.style.backgroundColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f, 0.3f));
                    AddElement(group);

                    for (int i = 0; i < groupNodes.Count; i++)
                    {
                        var node = groupNodes[i];
                        float nodeY = currentY + groupHeaderHeight + groupPadding + (i * nodeHeight);
                        float nodeX = level * colWidth + groupPadding;
                        
                        node.SetPosition(new Rect(nodeX, nodeY, 0, 0));
                        
                        AddElement(node); 
                        group.AddElement(node); 
                    }

                    float groupHeight = groupHeaderHeight + groupPadding * 2 + (groupNodes.Count * nodeHeight);
                    currentY += groupHeight + 20f; 
                }
            }

            // 3. 连线
            foreach (var edgeData in snapshot.edges)
            {
                if (nodeMap.TryGetValue(edgeData.sourceId, out var src) && nodeMap.TryGetValue(edgeData.targetId, out var dst))
                {
                    var edge = src.outputContainer.Q<Port>().ConnectTo(dst.inputContainer.Q<Port>());
                    AddElement(edge);
                }
            }
            
            // 重建后应用当前的 Filter 和 Heatmap 状态
            Window.ApplyFilter();
            Window.ApplyHeatmapSetting();

            schedule.Execute(() => FrameAll()).ExecuteLater(50);
        }
    }

    public class ReferenceViewerWindow : EditorWindow
    {
        private ReferenceGraphView _graphView;
        private RefSnapshot _currentSnapshot;
        
        private IntegerField _depthField;
        private Button _backButton;
        private ToolbarToggle _syncToggle;
        private ToolbarToggle _ignoreScriptToggle;
        private ToolbarToggle _heatmapToggle;
        private ToolbarSearchField _searchField;

        private Stack<string> _navigationHistory = new Stack<string>();
        private bool _isNavigatingBack = false;

        public bool SyncSelection => _syncToggle.value;

        [MenuItem("Tools/资源合规/通用/资源引用查看器 (Reference Viewer)")]
        public static void OpenWindow()
        {
            GetWindow<ReferenceViewerWindow>("资源引用查看器").Show();
        }

        [MenuItem("Assets/查看当前资源引用关系", false, 20)]
        public static void AnalyzeSelectedAsset()
        {
            var window = GetWindow<ReferenceViewerWindow>("资源引用查看器");
            window.Show();
            if (Selection.activeObject != null)
                window.NavigateTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(Selection.activeObject)), true);
        }

        private void OnEnable()
        {
            ConstructUI();
        }

        private void ConstructUI()
        {
            rootVisualElement.Clear();

            // === 顶部容器 (Container) ===
            // 使用 VisualElement 代替 Toolbar，支持 FlexWrap 自动换行
            var headerContainer = new VisualElement();
            headerContainer.style.flexDirection = FlexDirection.Row;
            headerContainer.style.flexWrap = Wrap.Wrap; // 关键：允许换行
            headerContainer.style.paddingTop = 5;
            headerContainer.style.paddingBottom = 5;
            headerContainer.style.paddingLeft = 5;
            headerContainer.style.backgroundColor = new StyleColor(new Color(0.22f, 0.22f, 0.22f));
            headerContainer.style.borderBottomWidth = 1;
            headerContainer.style.borderBottomColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            rootVisualElement.Add(headerContainer);

            // --- 区域 1: 导航与基础操作 ---
            var groupNav = CreateGroup(headerContainer);
            
            _backButton = new Button(NavigateBack) { text = "◀ 返回", tooltip = "返回上一个查看的资源" };
            _backButton.SetEnabled(false);
            _backButton.style.width = 60;
            groupNav.Add(_backButton);

            groupNav.Add(new Button(() => RefAnalyzer.Cache.BuildIndexFull()) { text = "索引重建", tooltip = "全量重建引用缓存" });

            // --- 区域 2: 显示设置 ---
            var groupSettings = CreateGroup(headerContainer);

            var depthLabel = new Label("深度:") { style = { alignSelf = Align.Center } };
            groupSettings.Add(depthLabel);
            
            _depthField = new IntegerField { value = EditorPrefs.GetInt("RefViewer_Depth", 1) };
            _depthField.style.width = 30;
            _depthField.RegisterValueChangedCallback(evt => { EditorPrefs.SetInt("RefViewer_Depth", evt.newValue); RefreshAnalysis(); });
            groupSettings.Add(_depthField);

            _ignoreScriptToggle = new ToolbarToggle { text = "No Script", tooltip = "隐藏脚本引用 (MonoScript, Shader)", value = EditorPrefs.GetBool("RefViewer_NoScripts", false) };
            _ignoreScriptToggle.RegisterValueChangedCallback(evt => { EditorPrefs.SetBool("RefViewer_NoScripts", evt.newValue); RefreshAnalysis(); });
            groupSettings.Add(_ignoreScriptToggle);

            // --- 区域 3: 高级视图 ---
            var groupView = CreateGroup(headerContainer);

            _heatmapToggle = new ToolbarToggle { text = "热力图", tooltip = "根据文件大小显示颜色 (绿<500KB, 黄<2MB, 红>2MB)", value = true };
            _heatmapToggle.RegisterValueChangedCallback(evt => ApplyHeatmapSetting());
            groupView.Add(_heatmapToggle);
            
            _syncToggle = new ToolbarToggle { text = "同步", tooltip = "与 Project 窗口同步选中项", value = true };
            groupView.Add(_syncToggle);

            // --- 区域 4: 工具与导出 ---
            var groupTools = CreateGroup(headerContainer);
            
            groupTools.Add(new Button(() => _graphView?.FrameAll()) { text = "聚焦" });
            groupTools.Add(new Button(ExportCSV) { text = "CSV" });
            groupTools.Add(new Button(OpenOrphanScanner) { text = "零引用资源扫描" });

            // --- 搜索栏 (独立一行，或者放在最后) ---
            var searchContainer = new VisualElement();
            searchContainer.style.flexGrow = 1;
            searchContainer.style.minWidth = 150;
            searchContainer.style.marginLeft = 10;
            searchContainer.style.marginRight = 10;
            searchContainer.style.justifyContent = Justify.Center;
            
            _searchField = new ToolbarSearchField();
            _searchField.style.width = StyleKeyword.Auto; // 自动充满剩余空间
            _searchField.RegisterValueChangedCallback(evt => _graphView?.FilterNodes(evt.newValue));
            searchContainer.Add(_searchField);
            
            headerContainer.Add(searchContainer); // 加到顶部容器里，作为最后一个元素

            // === Graph View ===
            _graphView = new ReferenceGraphView(this);
            rootVisualElement.Add(_graphView);
        }

        // 辅助方法：创建按钮组
        private VisualElement CreateGroup(VisualElement parent)
        {
            var group = new VisualElement();
            group.style.flexDirection = FlexDirection.Row;
            group.style.marginRight = 10;
            group.style.marginBottom = 2; // 防止换行时垂直间距太小
            parent.Add(group);
            return group;
        }

        public void ApplyFilter()
        {
            if (_searchField != null) _graphView?.FilterNodes(_searchField.value);
        }

        public void ApplyHeatmapSetting()
        {
            _graphView?.ToggleHeatmap(_heatmapToggle.value);
        }

        public void RefreshAnalysis()
        {
            if (_currentSnapshot != null) AnalyzeAsset(_currentSnapshot.rootAssetGuid);
        }

        public void NavigateTo(string guid, bool clearHistory = false)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!_isNavigatingBack && _currentSnapshot != null && _currentSnapshot.rootAssetGuid != guid)
            {
                if (clearHistory) _navigationHistory.Clear();
                else _navigationHistory.Push(_currentSnapshot.rootAssetGuid);
            }
            _isNavigatingBack = false;
            UpdateUIState();
            AnalyzeAsset(guid);
        }

        public void NavigateBack()
        {
            if (_navigationHistory.Count > 0)
            {
                _isNavigatingBack = true;
                NavigateTo(_navigationHistory.Pop());
            }
        }

        private void UpdateUIState()
        {
            if (_backButton != null)
            {
                _backButton.SetEnabled(_navigationHistory.Count > 0);
                // 简化按钮文字防止过长
                _backButton.text = _navigationHistory.Count > 0 ? $"◀ ({_navigationHistory.Count})" : "◀ 返回";
            }
        }

        public void AnalyzeAsset(string rootGuid)
        {
            if (!RefAnalyzer.Cache.HasIndex)
            {
                if (EditorUtility.DisplayDialog("索引缺失", "当前尚未构建引用索引，是否立即构建？", "构建", "跳过"))
                    RefAnalyzer.Cache.BuildIndexFull();
            }

            _currentSnapshot = RefAnalyzer.Analyze(rootGuid, _depthField.value, _ignoreScriptToggle.value);
            _graphView.RebuildGraph(_currentSnapshot);
        }

        private void ExportCSV()
        {
            if (_currentSnapshot == null) return;
            string path = EditorUtility.SaveFilePanel("导出 CSV", Application.dataPath, _currentSnapshot.snapshotName, "csv");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine("GUID,Path,Type,Level,Relation,Size(Bytes)");
            foreach (var node in _currentSnapshot.nodes)
            {
                string relation = node.level == 0 ? "Root" : (node.level < 0 ? "Referencer" : "Dependency");
                sb.AppendLine($"{node.id},{node.path},{node.typeName},{node.level},{relation},{node.fileSize}");
            }
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"CSV 已导出: {path}");
        }

        private void OpenOrphanScanner()
        {
            OrphanAssetWindow.Open();
        }
    }

    // ===================================================================================
    // 6. [FIXED LAYOUT] 零引用资源扫描窗口 (Orphan Asset Window)
    //    Fix: 解决列表数量过多时挤压顶部和底部的问题 (FlexShrink = 0)
    // ===================================================================================
    public class OrphanAssetWindow : EditorWindow
    {
        // Data
        private Dictionary<string, List<string>> _groupedOrphans = new Dictionary<string, List<string>>();
        private List<string> _displayingAssetGuids = new List<string>();
        private List<string> _allTypes = new List<string>();
        private string _scanPath = "Assets";

        // UI Elements
        private TextField _pathField;
        private ListView _typeListView;
        private ListView _assetListView;
        private Label _statusLabel;
        private Button _deleteSelectedBtn;
        private VisualElement _rightPanel;

        public static void Open()
        {
            var win = GetWindow<OrphanAssetWindow>("零引用资源扫描");
            win.minSize = new Vector2(800, 500);
            win.Show();
            if (RefAnalyzer.Cache.HasIndex)
            {
                win.ScanOrphans();
            }
        }

        private void OnEnable()
        {
            CreateGUI();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.backgroundColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));
            // [关键修复] 确保根节点占满窗口并使用列布局
            root.style.flexGrow = 1;
            root.style.flexDirection = FlexDirection.Column;

            // ===========================================
            // 1. 顶部工具栏 (Header)
            // ===========================================
            var headerContainer = new VisualElement();
            headerContainer.style.flexDirection = FlexDirection.Row;
            headerContainer.style.paddingTop = 5;
            headerContainer.style.paddingBottom = 5;
            headerContainer.style.paddingLeft = 10;
            headerContainer.style.paddingRight = 10;
            headerContainer.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));
            headerContainer.style.borderBottomWidth = 1;
            headerContainer.style.borderBottomColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            headerContainer.style.height = 40;
            headerContainer.style.alignItems = Align.Center;
            
            // [关键修复] 禁止 Header 被压缩
            headerContainer.style.flexShrink = 0; 
            
            root.Add(headerContainer);

            // Icon & Title
            var icon = new Image { image = EditorGUIUtility.IconContent("d_console.warnicon.sml").image };
            icon.style.marginRight = 5;
            headerContainer.Add(icon);
            
            var titleLabel = new Label("零引用资源");
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginRight = 15;
            headerContainer.Add(titleLabel);

            // Path Input
            _pathField = new TextField();
            _pathField.value = _scanPath;
            _pathField.style.width = 300;
            _pathField.RegisterValueChangedCallback(evt => _scanPath = evt.newValue);
            headerContainer.Add(_pathField);

            // Browse Button
            var browseBtn = new Button(() =>
            {
                string p = EditorUtility.OpenFolderPanel("选择扫描目录", "Assets", "");
                if (!string.IsNullOrEmpty(p) && p.StartsWith(Application.dataPath))
                        _pathField.value = "Assets" + p.Substring(Application.dataPath.Length);
            }) { text = "..." };
            browseBtn.style.width = 25;
            headerContainer.Add(browseBtn);

            // Scan Button
            var scanBtn = new Button(ScanOrphans) { text = "重新扫描" };
            scanBtn.style.marginLeft = 10;
            scanBtn.style.height = 24;
            headerContainer.Add(scanBtn);
            
            // Spacer
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            headerContainer.Add(spacer);

            var helpLabel = new Label("请确认非动态加载资源");
            helpLabel.style.color = new StyleColor(Color.gray);
            helpLabel.style.fontSize = 10;
            headerContainer.Add(helpLabel);


            // ===========================================
            // 2. 主体内容区域 (Split View: Left & Right)
            // ===========================================
            var bodyContainer = new VisualElement();
            bodyContainer.style.flexDirection = FlexDirection.Row;
            
            // [关键修复] Body 占据剩余所有空间，并禁止溢出，强制内部滚动
            bodyContainer.style.flexGrow = 1; 
            bodyContainer.style.flexShrink = 1; 
            bodyContainer.style.overflow = Overflow.Hidden; 
            
            root.Add(bodyContainer);

            // --- 左侧：类型列表 (Sidebar) ---
            var leftPanel = new VisualElement();
            leftPanel.style.width = 220;
            leftPanel.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.18f));
            leftPanel.style.borderRightWidth = 1;
            leftPanel.style.borderRightColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f));
            
            // [关键修复] 左侧面板也不允许被压缩
            leftPanel.style.flexShrink = 0; 
            
            bodyContainer.Add(leftPanel);

            var typeHeader = new Label("资源类型分类");
            typeHeader.style.paddingLeft = 5;
            typeHeader.style.paddingTop = 5;
            typeHeader.style.paddingBottom = 5;
            typeHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            typeHeader.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            // 防止表头被压缩
            typeHeader.style.flexShrink = 0;
            leftPanel.Add(typeHeader);

            _typeListView = new ListView();
            _typeListView.style.flexGrow = 1; // 占满左侧剩余高度
            _typeListView.itemHeight = 24;
            _typeListView.makeItem = () => new Label { style = { paddingLeft = 10, unityTextAlign = TextAnchor.MiddleLeft } };
            _typeListView.bindItem = (e, i) => 
            {
                var label = e as Label;
                if (i < _allTypes.Count)
                {
                    string typeKey = _allTypes[i];
                    int count = _groupedOrphans[typeKey].Count;
                    label.text = $"{typeKey} ({count})";
                }
            };
            _typeListView.itemsSource = _allTypes;
            _typeListView.selectionType = SelectionType.Single;
            _typeListView.onSelectionChange += OnTypeSelectionChanged;
            leftPanel.Add(_typeListView);


            // --- 右侧：资源详情列表 (Content) ---
            _rightPanel = new VisualElement();
            _rightPanel.style.flexGrow = 1;
            _rightPanel.style.flexShrink = 1; // 允许在窗口极小时收缩
            _rightPanel.style.backgroundColor = new StyleColor(new Color(0.2f, 0.2f, 0.2f));
            _rightPanel.style.overflow = Overflow.Hidden; // 确保右侧内容不溢出
            bodyContainer.Add(_rightPanel);

            // 右侧表头
            var listHeader = new VisualElement();
            listHeader.style.flexDirection = FlexDirection.Row;
            listHeader.style.height = 24;
            listHeader.style.backgroundColor = new StyleColor(new Color(0.22f, 0.22f, 0.22f));
            listHeader.style.paddingLeft = 5;
            listHeader.style.alignItems = Align.Center;
            listHeader.style.flexShrink = 0; // 禁止表头压缩
            listHeader.Add(new Label("Asset Path") { style = { unityFontStyleAndWeight = FontStyle.Bold, color = new StyleColor(Color.gray) } });
            _rightPanel.Add(listHeader);

            // 右侧列表
            _assetListView = new ListView();
            _assetListView.style.flexGrow = 1; // 占满右侧剩余高度
            _assetListView.itemHeight = 28;
            _assetListView.makeItem = MakeAssetItem;
            _assetListView.bindItem = BindAssetItem;
            _assetListView.itemsSource = _displayingAssetGuids;
            _assetListView.selectionType = SelectionType.Multiple;
            _assetListView.onSelectionChange += (items) => UpdateFooterState();
            
            _assetListView.RegisterCallback<MouseDownEvent>(evt => {
                if(evt.clickCount == 2 && _assetListView.selectedItem != null)
                    PingGuid((string)_assetListView.selectedItem);
            });
            _assetListView.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("删除选中文件", a => DeleteSelectedAssets(), 
                    _assetListView.selectedItems.Any() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("定位文件", a => {
                    if (_assetListView.selectedItem != null) PingGuid((string)_assetListView.selectedItem);
                });
            }));

            _rightPanel.Add(_assetListView);


            // ===========================================
            // 3. 底部状态栏 (Footer)
            // ===========================================
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.height = 36;
            footer.style.alignItems = Align.Center;
            footer.style.paddingLeft = 10;
            footer.style.paddingRight = 10;
            footer.style.backgroundColor = new StyleColor(new Color(0.22f, 0.22f, 0.22f));
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            
            // [关键修复] 禁止 Footer 被压缩
            footer.style.flexShrink = 0; 
            
            root.Add(footer);

            _statusLabel = new Label("就绪");
            _statusLabel.style.flexGrow = 1;
            footer.Add(_statusLabel);

            _deleteSelectedBtn = new Button(DeleteSelectedAssets) { text = "删除选中项" };
            _deleteSelectedBtn.style.height = 24;
            _deleteSelectedBtn.style.backgroundColor = new StyleColor(new Color(0.6f, 0.2f, 0.2f));
            _deleteSelectedBtn.style.color = new StyleColor(Color.white);
            _deleteSelectedBtn.SetEnabled(false);
            footer.Add(_deleteSelectedBtn);
        }

        // ===========================================
        // Logic & Scanning
        // ===========================================

        private void ScanOrphans()
        {
            if (!RefAnalyzer.Cache.HasIndex)
            {
                if(EditorUtility.DisplayDialog("提示", "需要先构建索引才能扫描零引用资源。", "立即构建", "取消"))
                    RefAnalyzer.Cache.BuildIndexFull();
                else return;
            }

            // 1. 获取所有零引用 GUID
            if (string.IsNullOrEmpty(_scanPath)) _scanPath = "Assets";
            var rawOrphans = RefAnalyzer.Cache.GetOrphanCandidates(_scanPath);

            // 2. 按类型分组
            _groupedOrphans.Clear();
            _allTypes.Clear();
            _displayingAssetGuids.Clear();

            int totalCount = 0;

            foreach (var guid in rawOrphans)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                Type assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                string typeName = assetType != null ? assetType.Name : "Unknown";

                if (!_groupedOrphans.ContainsKey(typeName))
                    _groupedOrphans[typeName] = new List<string>();

                _groupedOrphans[typeName].Add(guid);
                totalCount++;
            }

            _allTypes = _groupedOrphans.Keys.OrderBy(k => k).ToList();
            
            _typeListView.itemsSource = _allTypes;
            _typeListView.Rebuild();

            if (_allTypes.Count > 0)
            {
                _typeListView.SetSelection(0);
            }
            else
            {
                 _assetListView.Rebuild();
            }

            _statusLabel.text = $"扫描完成。共发现 {totalCount} 个零引用资源，涉及 {_allTypes.Count} 种类型。";
            UpdateFooterState();
        }

        private void OnTypeSelectionChanged(IEnumerable<object> selectedItems)
        {
            _displayingAssetGuids.Clear();
            
            var selectedList = selectedItems.Cast<string>().ToList();
            if (selectedList.Count > 0)
            {
                string typeName = selectedList[0];
                if (_groupedOrphans.ContainsKey(typeName))
                {
                    _displayingAssetGuids.AddRange(_groupedOrphans[typeName]);
                }
            }

            _assetListView.Rebuild();
            _assetListView.ClearSelection();
            UpdateFooterState();
        }

        // ===========================================
        // List Item Logic
        // ===========================================

        private VisualElement MakeAssetItem()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 5;
            row.style.paddingRight = 5;

            var icon = new Image { name = "icon" };
            icon.style.width = 16; icon.style.height = 16; icon.style.marginRight = 5;
            icon.style.flexShrink = 0; // 防止图标被压缩
            row.Add(icon);

            var label = new Label { name = "label" };
            label.style.flexGrow = 1;
            label.style.flexShrink = 1; 
            label.style.overflow = Overflow.Hidden;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
            row.Add(label);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexShrink = 0; // 按钮区不压缩
            actions.style.marginLeft = 5;

            var pingBtn = new Button() { name = "pingBtn", tooltip = "定位" };
            pingBtn.style.width = 25; pingBtn.style.height = 20;
            pingBtn.Add(new Image { image = EditorGUIUtility.IconContent("d_ViewToolOrbit").image });
            pingBtn.clicked += () => { if(pingBtn.userData is string guid) PingGuid(guid); };
            actions.Add(pingBtn);

            var delBtn = new Button() { name = "delBtn", tooltip = "删除" };
            delBtn.style.width = 25; delBtn.style.height = 20; delBtn.style.marginLeft = 2;
            delBtn.Add(new Image { image = EditorGUIUtility.IconContent("TreeEditor.Trash").image });
            delBtn.clicked += () => { if(delBtn.userData is string guid) DeleteSingleAsset(guid); };
            actions.Add(delBtn);

            row.Add(actions);
            return row;
        }

        private void BindAssetItem(VisualElement element, int index)
        {
            if (index >= _displayingAssetGuids.Count) return;
            string guid = _displayingAssetGuids[index];
            string path = AssetDatabase.GUIDToAssetPath(guid);

            element.Q<Button>("pingBtn").userData = guid;
            element.Q<Button>("delBtn").userData = guid;

            var icon = element.Q<Image>("icon");
            var label = element.Q<Label>("label");
            
            Texture2D cachedIcon = AssetDatabase.GetCachedIcon(path) as Texture2D;
            if (cachedIcon == null) cachedIcon = EditorGUIUtility.IconContent("DefaultAsset Icon").image as Texture2D;
            icon.image = cachedIcon;
            
            label.text = path;
            label.tooltip = path;

            bool isSelected = _assetListView.selectedIndices.Contains(index);
            if (!isSelected)
            {
                element.style.backgroundColor = (index % 2 == 0) 
                    ? new StyleColor(Color.clear) 
                    : new StyleColor(new Color(1f, 1f, 1f, 0.03f));
            }
            else
            {
                element.style.backgroundColor = new StyleColor(new Color(0.24f, 0.37f, 0.58f, 1f));
            }
        }

        // ===========================================
        // Operations
        // ===========================================

        private void PingGuid(string guid)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(guid));
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }

        private void DeleteSingleAsset(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (EditorUtility.DisplayDialog("删除确认", $"确认删除文件？\n{path}", "删除", "取消"))
            {
                AssetDatabase.DeleteAsset(path);
                
                string typeToRemove = null;
                foreach(var kvp in _groupedOrphans) {
                    if (kvp.Value.Remove(guid)) { typeToRemove = kvp.Key; break; }
                }

                _displayingAssetGuids.Remove(guid);
                
                if (typeToRemove != null && _groupedOrphans[typeToRemove].Count == 0)
                {
                    _groupedOrphans.Remove(typeToRemove);
                    _allTypes.Remove(typeToRemove);
                    _typeListView.Rebuild();
                }
                else 
                {
                    _typeListView.RefreshItems(); 
                }

                _assetListView.Rebuild();
                UpdateFooterState();
            }
        }

        private void DeleteSelectedAssets()
        {
            var selectedGuids = _assetListView.selectedItems.Cast<string>().ToList();
            if (selectedGuids.Count == 0) return;

            if (EditorUtility.DisplayDialog("批量删除", $"确定要删除选中的 {selectedGuids.Count} 个文件吗？", "删除全部", "取消"))
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var guid in selectedGuids)
                    {
                        AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
                        foreach(var list in _groupedOrphans.Values) list.Remove(guid);
                        _displayingAssetGuids.Remove(guid);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }

                ScanOrphans(); 
            }
        }

        private void UpdateFooterState()
        {
            int count = _assetListView.selectedIndices.Count();
            _deleteSelectedBtn.text = count > 0 ? $"删除选中项 ({count})" : "删除选中项";
            _deleteSelectedBtn.SetEnabled(count > 0);
            
            if (_assetListView.itemsSource != null)
            {
                _statusLabel.text = $"当前显示: {_assetListView.itemsSource.Count} 个资源";
            }
        }
    }
    // ===================================================================================
    // 7. 替换工具窗口 (Replacement Window)
    // ===================================================================================
    public class ReferenceReplacementWindow : EditorWindow
    {
        private string _targetGuid;
        private Object _targetAsset;
        private Object _newAsset;
        private List<string> _referencers = new List<string>();

        public static void Open(string guid)
        {
            var window = GetWindow<ReferenceReplacementWindow>(true, "替换引用", true);
            window._targetGuid = guid;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            window._targetAsset = AssetDatabase.LoadAssetAtPath<Object>(path);
            window._newAsset = null;
            
            if (RefAnalyzer.Cache.HasIndex)
            {
                window._referencers = RefAnalyzer.Cache.GetReferencers(guid);
            }
            
            window.minSize = new Vector2(350, 250);
            window.ShowUtility(); 
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("引用替换工具", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            EditorGUILayout.LabelField("当前资源 (被替换):");
            GUI.enabled = false;
            EditorGUILayout.ObjectField(_targetAsset, typeof(Object), false);
            GUI.enabled = true;
            
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("新资源 (替换为):");
            _newAsset = EditorGUILayout.ObjectField(_newAsset, typeof(Object), false);

            EditorGUILayout.Space(10);
            
            EditorGUILayout.HelpBox($"将影响 {_referencers.Count} 个引用此资源的 Asset。", MessageType.Info);
            
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("执行替换", GUILayout.Height(30)))
            {
                if (_targetAsset == null || _newAsset == null)
                {
                    EditorUtility.DisplayDialog("错误", "资源不能为空。", "确定");
                    return;
                }
                
                if (_targetAsset.GetType() != _newAsset.GetType())
                {
                      bool confirmType = EditorUtility.DisplayDialog("类型不匹配", 
                          $"目标类型为 {_targetAsset.GetType().Name}, 新资源类型为 {_newAsset.GetType().Name}.\n强制替换可能会破坏引用，是否继续？", 
                          "是", "取消");
                      if (!confirmType) return;
                }

                if (EditorUtility.DisplayDialog("确认替换", 
                    $"确定要在 {_referencers.Count} 个资源中替换引用吗？\n建议先备份项目！", 
                    "替换", "取消"))
                {
                    PerformReplacement();
                }
            }
            EditorGUILayout.Space(10);
        }

        private void PerformReplacement()
        {
            int count = 0;
            try
            {
                int total = _referencers.Count;
                for (int i = 0; i < total; i++)
                {
                    string refGuid = _referencers[i];
                    string path = AssetDatabase.GUIDToAssetPath(refGuid);
                    
                    EditorUtility.DisplayProgressBar("正在替换引用", $"处理中: {path}", (float)i / total);

                    Object refObj = AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (!refObj) continue;

                    SerializedObject so = new SerializedObject(refObj);
                    SerializedProperty sp = so.GetIterator();
                    bool changed = false;

                    while (sp.Next(true))
                    {
                        if (sp.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (sp.objectReferenceValue == _targetAsset)
                            {
                                sp.objectReferenceValue = _newAsset;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        so.ApplyModifiedProperties();
                        count++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (count > 0)
            {
                AssetDatabase.SaveAssets();
                var viewer = Resources.FindObjectsOfTypeAll<ReferenceViewerWindow>().FirstOrDefault();
                if (viewer != null) viewer.RefreshAnalysis();
                
                EditorUtility.DisplayDialog("成功", $"已在 {count} 个资源中替换引用。", "确定");
                Close();
            }
            else
            {
                EditorUtility.DisplayDialog("提示", "未找到可替换的引用 (可能已被修改或未保存)。", "确定");
            }
        }
    }
}
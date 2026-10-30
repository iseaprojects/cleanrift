using System.Drawing;
using System.Text;
using System.Xml.Linq;
using CleanRift.Core;
using CleanRift.UI;

namespace CleanRift.UI.Controls;

/// <summary>
/// Checkable tree of leftover file system and registry items, used by the
/// Removal Wizard and Force Removal.
/// </summary>
public sealed class LeftoverTreeControl : UserControl
{
    private readonly TreeView _tree;
    private bool _updating;
    private ScanResult? _scan;
    private readonly Dictionary<TreeNode, LeftoverItem> _map = new();

    public event Action? SummaryChanged;

    public LeftoverTreeControl()
    {
        var theme = Theme.Current;
        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            FullRowSelect = false,
            ShowLines = false,
            ShowPlusMinus = true,
            ShowRootLines = true,
            ShowNodeToolTips = true,
            BackColor = theme.ListBack,
            ForeColor = theme.Text,
            Font = new Font("Segoe UI", 9f),
            ItemHeight = 20
        };

        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        images.Images.Add("folder", AppIcons.Get(AppIcons.FolderSearch, 16) ?? new Bitmap(16, 16));
        images.Images.Add("file", AppIcons.Get(AppIcons.Registry, 16) ?? new Bitmap(16, 16));
        images.Images.Add("key", AppIcons.Get(AppIcons.Registry, 16) ?? new Bitmap(16, 16));
        images.Images.Add("value", AppIcons.Get(369, 16) ?? new Bitmap(16, 16));
        _tree.ImageList = images;

        _tree.AfterCheck += TreeAfterCheck;
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is LeftoverItem { Kind: LeftoverKind.File or LeftoverKind.Folder } item)
            {
                if (File.Exists(item.FullPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            "explorer.exe"),
                        Arguments = "/select,\"" + item.FullPath + "\"",
                        UseShellExecute = true
                    });
            }
        };

        Controls.Add(_tree);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkScrollbars(_tree);
    }

    public ScanResult? Scan => _scan;

    public void Clear()
    {
        _tree.Nodes.Clear();
        _map.Clear();
        _scan = null;
        SummaryChanged?.Invoke();
    }

    public new void Load(ScanResult scan)
    {
        _scan = scan;
        _updating = true;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            _map.Clear();

            var fsNode = _tree.Nodes.Add(LanguageManager.Instance.F("UninstallWizard/treeItemFileSystem",
                scan.FileSystemRoot.Children.Sum(c => c.TotalCount)));
            fsNode.ImageKey = fsNode.SelectedImageKey = "folder";
            fsNode.Tag = scan.FileSystemRoot;
            fsNode.Checked = true;
            foreach (var child in scan.FileSystemRoot.Children)
                AddNode(fsNode, child);

            var regNode = _tree.Nodes.Add(LanguageManager.Instance.F("UninstallWizard/treeItemRegistry",
                scan.RegistryRoot.Children.Sum(c => c.TotalCount)));
            regNode.ImageKey = regNode.SelectedImageKey = "key";
            regNode.Tag = scan.RegistryRoot;
            regNode.Checked = true;
            foreach (var child in scan.RegistryRoot.Children)
                AddNode(regNode, child);

            _tree.ExpandAll();
        }
        finally
        {
            _tree.EndUpdate();
            _updating = false;
        }

        SummaryChanged?.Invoke();
    }

    private TreeNode AddNode(TreeNode parent, LeftoverItem item)
    {
        var text = item.Kind switch
        {
            LeftoverKind.RegistryValue => $"{item.Name} = {Clamp(item.ValueData, 90)}",
            _ => item.Name
        };
        var image = item.Kind switch
        {
            LeftoverKind.Folder => "folder",
            LeftoverKind.RegistryKey => "key",
            LeftoverKind.RegistryValue => "value",
            _ => "file"
        };
        var node = parent.Nodes.Add(text);
        node.ImageKey = node.SelectedImageKey = image;
        node.Tag = item;
        node.Checked = item.Checked;
        node.ToolTipText = item.DetailText;
        _map[node] = item;
        foreach (var child in item.Children)
            AddNode(node, child);
        return node;
    }

    private static string Clamp(string value, int length)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= length ? value : value[..length] + "...";
    }

    private void TreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updating || e.Node == null)
            return;

        _updating = true;
        try
        {
            SetChildren(e.Node, e.Node.Checked);
            UpdateAncestors(e.Node);
            SyncItems();
        }
        finally
        {
            _updating = false;
        }

        SummaryChanged?.Invoke();
    }

    private void SyncItems()
    {
        foreach (var (node, item) in _map)
            item.Checked = node.Checked;
        if (_scan != null)
        {
            _scan.FileSystemRoot.Checked = true;
            _scan.RegistryRoot.Checked = true;
        }
    }

    private static void SetChildren(TreeNode node, bool check)
    {
        foreach (TreeNode child in node.Nodes)
        {
            child.Checked = check;
            SetChildren(child, check);
        }
    }

    private static void UpdateAncestors(TreeNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            var all = parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
            if (parent.Checked != all)
                parent.Checked = all;
            parent = parent.Parent;
        }
    }

    public List<RemovalJobItem> CollectChecked()
    {
        var result = new List<RemovalJobItem>();
        if (_scan == null)
            return result;
        result.AddRange(RemovalEngine.Collect(_scan.FileSystemRoot));
        result.AddRange(RemovalEngine.Collect(_scan.RegistryRoot));
        return result;
    }

    public (int files, int registry, long size) GetCheckedSummary()
    {
        var files = 0;
        var registry = 0;
        long size = 0;
        if (_scan == null)
            return (0, 0, 0);
        CountItems(_scan.FileSystemRoot, ref files, ref size, registry: false, ref registry);
        CountItems(_scan.RegistryRoot, ref files, ref size, registry: true, ref registry);
        return (files, registry, size);
    }

    private static void CountItems(LeftoverItem item, ref int files, ref long size, bool registry,
        ref int registryCount)
    {
        if (!item.Checked)
            return;

        if (registry)
        {
            if (item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue)
                registryCount++;
        }
        else
        {
            if (item.Kind == LeftoverKind.File)
                files++;
            else if (item.Kind == LeftoverKind.Folder && !string.IsNullOrEmpty(item.FullPath))
                files++;
            size += item.Kind == LeftoverKind.File ? item.Size : 0;
        }

        foreach (var child in item.Children)
            CountItems(child, ref files, ref size, registry, ref registryCount);
    }

    public void SaveXml(string path, string programName)
    {
        if (_scan == null)
            return;

        var root = new XElement("UninstallWizardScan",
            new XAttribute("program", programName),
            new XAttribute("generated", DateTime.Now.ToString("O")));
        root.Add(ToXml(_scan.FileSystemRoot, "FileSystem"));
        root.Add(ToXml(_scan.RegistryRoot, "Registry"));
        new XDocument(root).Save(path);
    }

    private static XElement ToXml(LeftoverItem item, string name)
    {
        var element = new XElement(name,
            new XAttribute("type", item.Kind.ToString()),
            new XAttribute("checked", item.Checked));
        if (!string.IsNullOrEmpty(item.FullPath))
            element.Add(new XAttribute("path", item.FullPath));
        if (!string.IsNullOrEmpty(item.ValueName))
            element.Add(new XAttribute("name", item.ValueName));
        if (!string.IsNullOrEmpty(item.ValueData))
            element.Add(new XAttribute("data", item.ValueData));
        foreach (var child in item.Children)
            element.Add(ToXml(child, "Item"));
        return element;
    }

    public void SaveLog(string path, IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
            builder.AppendLine(line);
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }
}

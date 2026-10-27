using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YourLocalLib.Core;

namespace YourLocalLib.ViewModels;

public partial class ShelvesViewModel : ObservableObject
{
    private readonly LibraryDatabase _db;
    private readonly AppEvents _events;
    private readonly HashSet<long> _collapsed = new();
    private List<LocationInfo> _all = new();
    private bool _rebuilding;

    public ShelvesViewModel(LibraryDatabase db, AppEvents events)
    {
        _db = db;
        _events = events;
    }

    public ObservableCollection<ShelfRow> Shelves { get; } = new();
    public ObservableCollection<ShelfChoice> Destinations { get; } = new();
    public ObservableCollection<ShelfChoice> Parents { get; } = new();

    [ObservableProperty] private ShelfRow? selected;
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private ShelfChoice? newParent;
    [ObservableProperty] private string rename = "";
    [ObservableProperty] private ShelfChoice? deleteDestination;
    [ObservableProperty] private ShelfChoice? selectedParent;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string? message;
    [ObservableProperty] private bool empty = true;

    public Func<Task<string?>>? PickPdf { get; set; }

    public void Reset()
    {
        Selected = null;
        _collapsed.Clear();
        NewName = "";
        Rename = "";
        NewParent = null;
        DeleteDestination = null;
        SelectedParent = null;
        Message = null;
        Error = null;
        Refresh();
    }

    public void Refresh()
    {
        var selectedId = Selected?.Id;
        var newParentId = NewParent?.ShelfId;
        var deleteId = DeleteDestination?.ShelfId;
        _all = _db.Locations().ToList();
        _collapsed.RemoveWhere(id => _all.All(shelf => shelf.Id != id));
        if (selectedId is long show)
            Reveal(show);
        Parents.Clear();
        Destinations.Clear();
        Parents.Add(new ShelfChoice("No parent", "parent", null));
        Destinations.Add(new ShelfChoice("Leave them unlocated", "none", null));
        foreach (var shelf in _all)
        {
            Parents.Add(new ShelfChoice(shelf.Path, "parent", shelf.Id));
            Destinations.Add(new ShelfChoice(shelf.Path, "dest", shelf.Id));
        }
        NewParent = Parents.FirstOrDefault(p => p.ShelfId == newParentId) ?? Parents[0];
        DeleteDestination = Destinations.FirstOrDefault(d => d.ShelfId == deleteId) ?? Destinations[0];
        Error = null;
        RebuildShelves(selectedId);
    }

    private void ToggleShelf(ShelfRow row)
    {
        if (!row.HasChildren)
            return;
        if (!_collapsed.Add(row.Id))
            _collapsed.Remove(row.Id);
        var selectedId = Selected?.Id;
        if (selectedId is long id && _collapsed.Contains(row.Id) && IsHidden(id))
            selectedId = row.Id;
        RebuildShelves(selectedId);
    }

    private void RebuildShelves(long? selectedId)
    {
        _rebuilding = true;
        Shelves.Clear();
        foreach (var shelf in _all)
        {
            if (IsHidden(shelf.Id))
                continue;
            var inside = CountDescendants(shelf.Id);
            Shelves.Add(new ShelfRow(shelf, inside, _collapsed.Contains(shelf.Id), ToggleShelf));
        }
        Empty = _all.Count == 0;
        Selected = selectedId is long id ? Shelves.FirstOrDefault(s => s.Id == id) : null;
        _rebuilding = false;
    }

    private bool IsHidden(long id)
    {
        var byId = _all.ToDictionary(shelf => shelf.Id);
        var parent = byId.TryGetValue(id, out var info) ? info.ParentId : null;
        var guard = 0;
        while (parent is long pid && guard++ < 64)
        {
            if (_collapsed.Contains(pid))
                return true;
            parent = byId.TryGetValue(pid, out var ancestor) ? ancestor.ParentId : null;
        }
        return false;
    }

    private void Reveal(long id)
    {
        var byId = _all.ToDictionary(shelf => shelf.Id);
        var parent = byId.TryGetValue(id, out var info) ? info.ParentId : null;
        var guard = 0;
        while (parent is long pid && guard++ < 64)
        {
            _collapsed.Remove(pid);
            parent = byId.TryGetValue(pid, out var ancestor) ? ancestor.ParentId : null;
        }
    }

    private int CountDescendants(long id)
    {
        var count = 0;
        foreach (var child in _all.Where(shelf => shelf.ParentId == id))
            count += 1 + CountDescendants(child.Id);
        return count;
    }

    partial void OnSelectedChanged(ShelfRow? value)
    {
        if (_rebuilding && value == null)
            return;
        Rename = value?.Name ?? "";
        SelectedParent = value == null
            ? null
            : Parents.FirstOrDefault(p => p.ShelfId == value.ParentId) ?? Parents.FirstOrDefault();
    }

    [RelayCommand]
    private void Add()
    {
        try
        {
            _db.AddLocation(NewName, NewParent?.ShelfId);
            NewName = "";
            Message = "Shelf added. Print a label when you are ready to stick it on the bookcase.";
            Error = null;
            Refresh();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveName()
    {
        if (Selected == null)
            return;
        try
        {
            _db.RenameLocation(Selected.Id, Rename);
            Message = "Name saved.";
            Error = null;
            Refresh();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveParent()
    {
        if (Selected == null)
            return;
        try
        {
            _db.SetParent(Selected.Id, SelectedParent?.ShelfId);
            Error = null;
            Message = "Shelf moved.";
            var id = Selected.Id;
            Refresh();
            Selected = Shelves.FirstOrDefault(s => s.Id == id);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Nest(string direction)
    {
        if (Selected == null)
            return;
        try
        {
            if (direction == "in" && Shelves.FirstOrDefault(s => s.Id != Selected.Id) is ShelfRow other)
                _db.SetParent(Selected.Id, other.Id);
            if (direction == "out")
                _db.SetParent(Selected.Id, null);
            Error = null;
            Refresh();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Move(string direction)
    {
        if (Selected == null)
            return;
        _db.MoveLocation(Selected.Id, direction == "up" ? -1 : 1);
        var id = Selected.Id;
        Refresh();
        Selected = Shelves.FirstOrDefault(s => s.Id == id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected == null)
            return;
        try
        {
            _db.DeleteLocation(Selected.Id, DeleteDestination?.ShelfId);
            Message = "Shelf removed. Its QR code will not be reused.";
            Error = null;
            Refresh();
            _events.Changed();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveLabels()
    {
        if (PickPdf == null || Shelves.Count == 0)
        {
            Error = "Add a shelf before printing labels.";
            return;
        }
        var path = await PickPdf();
        if (path == null)
            return;
        try
        {
            LabelService.WritePdf(path, _db.Locations());
            Message = "Labels saved. Print them large enough to read from the bookcase.";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

public sealed class ShelfRow
{
    public ShelfRow(LocationInfo info, int inside, bool collapsed, Action<ShelfRow> toggle)
    {
        Id = info.Id;
        ParentId = info.ParentId;
        Name = info.Name;
        Path = info.Path;
        Code = info.Code;
        Display = new string('\u2003', info.Depth) + info.Path;
        HasChildren = inside > 0;
        FoldLabel = !HasChildren ? "" : collapsed ? "Show " + inside : "Hide";
        Toggle = new RelayCommand(() => toggle(this));
    }

    public long Id { get; }
    public long? ParentId { get; }
    public string Name { get; }
    public string Path { get; }
    public string Code { get; }
    public string Display { get; }
    public bool HasChildren { get; }
    public string FoldLabel { get; }
    public IRelayCommand Toggle { get; }
}

﻿using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using MemoryProfilerWindow;
using Assets.Editor.Treemap;
using UnityEditorInternal;

using System.IO;
using System.Text;
using LitJson;
using System.Diagnostics;


public struct sDiffType
{
    public static readonly string AdditiveType = "(added)";
    public static readonly string NegativeType = "(removed)";
    public static readonly string ModificationType = "(modified)";
}

public class MemCategory
{
    public int Category;
    public int Count;
    public int Size;
}

public class MemType
{
    public string TypeName = "Foo";
    public int Count = 0;
    public int Size = 0;
    public string SizeLiterally = "";

    public List<object> Objects = new List<object>();

    public int Category = 0;

    public void AddObject(MemObject mo)
    {
        Objects.Add(mo);
        Count = Objects.Count;
        Size += mo.Size;
        SizeLiterally = EditorUtility.FormatBytes(Size);
    }
}

public class MemObject
{
    public string InstanceName;
    public int Size = 0;
    public int RefCount = 0;

    public MemObject(ThingInMemory thing, CrawledMemorySnapshot unpacked)
    {
        _thing = thing;

        if (_thing != null)
        {
            var mo = thing as ManagedObject;

            if (mo != null && mo.typeDescription.name == "System.String")
            {
                InstanceName = StringTools.ReadString(unpacked.managedHeap.Find(mo.address, unpacked.virtualMachineInformation), unpacked.virtualMachineInformation);
            }
            else
            {
                InstanceName = _thing.caption;
            }

            Size = _thing.size;
            RefCount = _thing.referencedBy.Length;
        }
    }

    public ThingInMemory _thing;
}

public class MemTableBrowser
{
    CrawledMemorySnapshot _unpacked;
    CrawledMemorySnapshot _preUnpacked;
    UnPackedInfos _unpackedInfos;

    TableView _typeTable;
    TableView _objectTable;
    EditorWindow _hostWindow;

    private Dictionary<string, MemType> _types = new Dictionary<string, MemType>();
    private Dictionary<int, MemCategory> _categories = new Dictionary<int, MemCategory>();
    private string[] _categoryLiterals = new string[MemConst.MemTypeCategories.Length];

    private int _memTypeCategory = 0;
    private int _memTypeSizeLimiter = 0;

    string _searchInstanceString = "";
    string _searchTypeString = "";
    MemType _searchResultType;

    StaticDetailInfo _staticDetailInfo = new StaticDetailInfo();

    private class UnPackedInfos
    {
        CrawledMemorySnapshot unpacked; 
        CrawledMemorySnapshot preUnpacked;

        public Dictionary<int, NativeUnityEngineObject> _unpackedNativeDict= new Dictionary<int, NativeUnityEngineObject>();
        public Dictionary<int, NativeUnityEngineObject> _preUnpackedNativeDict = new Dictionary<int, NativeUnityEngineObject>();
        public Dictionary<int, ManagedObject> _unpackedManagedDict = new Dictionary<int, ManagedObject>();
        public Dictionary<int, ManagedObject> _preUnpackedManagedDict = new Dictionary<int,ManagedObject>();

        public UnPackedInfos(CrawledMemorySnapshot unpacked, CrawledMemorySnapshot preUnpacked)
        {
            this.unpacked = unpacked;
            this.preUnpacked = preUnpacked;
        }

        public void calculateCrawledDict()
        {
            if (preUnpacked == null)
                return;

            foreach (NativeUnityEngineObject nat in unpacked.nativeObjects)
            {
                if (nat == null)
                    continue;
                int key =getNativeObjHashCode(nat);
                if (!_unpackedNativeDict.ContainsKey(key))
                    _unpackedNativeDict.Add(key,nat);
            }

            foreach (NativeUnityEngineObject nat in preUnpacked.nativeObjects)
            {
                if (nat == null)
                    continue;
                int key = getNativeObjHashCode(nat);
                if (!_preUnpackedNativeDict.ContainsKey(key))
                    _preUnpackedNativeDict.Add(key, nat);
            }

            foreach (ManagedObject mao in unpacked.managedObjects)
            {
                if (mao == null)
                    continue;
                int key = getManagedObjHashCode(mao, unpacked);
                if (key == -1)
                    continue;
                if (!_unpackedManagedDict.ContainsKey(key))
                    _unpackedManagedDict.Add(key,mao);
            }

            foreach (ManagedObject mao in preUnpacked.managedObjects)
            {
                if (mao == null)
                    continue;
                int key = getManagedObjHashCode(mao, preUnpacked);
                if (key == -1)
                    continue;
                if (!_preUnpackedManagedDict.ContainsKey(key))
                    _preUnpackedManagedDict.Add(key,mao);
            }
        }

        int getNativeObjHashCode(NativeUnityEngineObject nat) 
        {
            return nat.instanceID.GetHashCode();
        }

        int getManagedObjHashCode(ManagedObject mao ,CrawledMemorySnapshot unpacked)
        {
            var ba =unpacked.managedHeap.Find(mao.address, unpacked.virtualMachineInformation);
            var result =getBytesFromHeap(ba,mao.size);
            if (result != null && result.Length>0)
                return result.GetHashCode() * mao.size;
            return -1;
        }

        byte[] getBytesFromHeap(BytesAndOffset ba, int size)
        {
            byte[] result = new byte[size];
            for (int i = 0; i < size;i++)
            {
                result[i] = ba.bytes[i+ba.offset];
            }
            return result;
        }
    }
    public MemTableBrowser(EditorWindow hostWindow)
    {
        _hostWindow = hostWindow;

        // create the table with a specified object type
        _typeTable = new TableView(hostWindow, typeof(MemType));
        _objectTable = new TableView(hostWindow, typeof(MemObject));

        // setup the description for content
        _typeTable.AddColumn("TypeName", "Type Name", 0.6f, TextAnchor.MiddleLeft);
        _typeTable.AddColumn("Count", "Count", 0.15f);
        _typeTable.AddColumn("Size", "Size", 0.25f, TextAnchor.MiddleCenter, PAEditorConst.BytesFormatter);

        _objectTable.AddColumn("InstanceName", "Instance Name", 0.8f, TextAnchor.MiddleLeft);
        _objectTable.AddColumn("Size", "Size", 0.1f, TextAnchor.MiddleCenter, PAEditorConst.BytesFormatter);
        _objectTable.AddColumn("RefCount", "Refs", 0.1f);

        // sorting
        _typeTable.SetSortParams(2, true);
        _objectTable.SetSortParams(1, true);

        // register the event-handling function
        _typeTable.OnSelected += OnTypeSelected;
        _objectTable.OnSelected += OnObjectSelected;
    }

    public void clearTableData() {
        _types.Clear();
        _categories.Clear();
        RefreshTables();
    }

    public void RefreshData(CrawledMemorySnapshot unpackedCrawl, CrawledMemorySnapshot preUnpackedCrawl = null)
    {
        MemUtil.LoadSnapshotProgress(0.01f, "refresh Data");
        _unpacked = unpackedCrawl;
        _preUnpacked = preUnpackedCrawl;
        _types.Clear();
        _categories.Clear();
        _staticDetailInfo.clear();
        foreach (ThingInMemory thingInMemory in _unpacked.allObjects)
        {
            string typeName = MemUtil.GetGroupName(thingInMemory);
            if (typeName.Length == 0)
                continue;

            int category = MemUtil.GetCategory(thingInMemory);

            MemObject item = new MemObject(thingInMemory, _unpacked);
            if (! _staticDetailInfo.isDetailStaticFileds(typeName, thingInMemory.caption,item.Size))
            {
                MemType theType;
                if (!_types.ContainsKey(typeName))
                {
                    theType = new MemType();
                    theType.TypeName = MemUtil.GetCategoryLiteral(thingInMemory) + typeName;
                    theType.Category = category;
                    theType.Objects = new List<object>();
                    _types.Add(typeName, theType);
                }
                else
                {
                    theType = _types[typeName];
                }
                theType.AddObject(item);
            }

            MemCategory theCategory;
            if (!_categories.TryGetValue(category, out theCategory))
            {
                theCategory = new MemCategory();
                theCategory.Category = category;
                _categories.Add(category, theCategory);
            }
            theCategory.Size += item.Size;
            theCategory.Count++;
        }

        int[] sizes = new int[MemConst.MemTypeCategories.Length];
        int[] counts = new int[MemConst.MemTypeCategories.Length];
        foreach (var item in _categories)
        {
            sizes[0] += item.Value.Size;
            counts[0] += item.Value.Count;

            if (item.Key == 1)
            {
                sizes[1] += item.Value.Size;
                counts[1] += item.Value.Count;
            }
            else if (item.Key == 2)
            {
                sizes[2] += item.Value.Size;
                counts[2] += item.Value.Count;
            }
            else
            {
                sizes[3] += item.Value.Size;
                counts[3] += item.Value.Count;
            }
        }

        for (int i = 0; i < _categoryLiterals.Length; i++)
        {
            _categoryLiterals[i] = string.Format("{0} ({1}, {2})", MemConst.MemTypeCategories[i], counts[i], EditorUtility.FormatBytes(sizes[i]));
        }

        MemUtil.LoadSnapshotProgress(0.4f, "unpack all objs");
        freshUnpackInfos();
        checkAddtiveThings();
        checkNegativeThings();
        MemUtil.LoadSnapshotProgress(0.8f, "check diff");
        RefreshTables();
        MemUtil.LoadSnapshotProgress(1.0f, "done");
    }

    void freshUnpackInfos()
    {
        if (_preUnpacked == null)
            return;
        _unpackedInfos = new UnPackedInfos(_unpacked, _preUnpacked);
        _unpackedInfos.calculateCrawledDict();
    }

    void checkAddtiveThings()
    {
        if (_preUnpacked == null || _unpackedInfos ==null)
            return;

        _checkDiffThings(sDiffType.AdditiveType, _unpacked,_unpackedInfos._unpackedNativeDict, _unpackedInfos._preUnpackedNativeDict,
        _unpackedInfos._unpackedManagedDict, _unpackedInfos._preUnpackedManagedDict);
    }

    void checkNegativeThings()
    {
        if (_preUnpacked == null || _unpackedInfos == null)
            return;
        _checkDiffThings(sDiffType.NegativeType, _preUnpacked,_unpackedInfos._preUnpackedNativeDict,_unpackedInfos._unpackedNativeDict
            , _unpackedInfos._preUnpackedManagedDict, _unpackedInfos._unpackedManagedDict);
    }
    private void _handleModifyObj(ref Dictionary<int, ManagedObject> exceptNativeDict, KeyValuePair<int, ManagedObject> orginNat, CrawledMemorySnapshot resultPacked)
    {
        ManagedObject exceptObj;
        exceptNativeDict.TryGetValue(orginNat.Key, out exceptObj);

        if (exceptObj.address == orginNat.Value.address)
        {
            _handleDiffManangeObj(orginNat.Value, sDiffType.ModificationType, resultPacked);
        }
    }

    void _checkDiffThings(string diffType, CrawledMemorySnapshot resultPacked, Dictionary<int,NativeUnityEngineObject> orginNativeDict,
        Dictionary<int,NativeUnityEngineObject> exceptNativeDict,Dictionary<int, ManagedObject> orginManageDict,
        Dictionary<int,ManagedObject> exceptManageDict)
    {
        if (_preUnpacked == null)
            return;

        foreach (var orginNat in orginNativeDict)
        {
            if (!exceptNativeDict.ContainsKey(orginNat.Key))
            {
                _handleDiffNativeObj(orginNat.Value, diffType, resultPacked);
            }
        }

        foreach (var orginMao in orginManageDict)
        {
            if (!exceptManageDict.ContainsKey(orginMao.Key))
            {
                _handleDiffManangeObj(orginMao.Value, diffType, resultPacked);
            }
            else
            {
                if (diffType != sDiffType.AdditiveType)
                    continue;
                _handleModifyObj(ref orginManageDict, orginMao, resultPacked);
            }
        }
    }

    void _handleDiffNativeObj(NativeUnityEngineObject nat, string diffType, CrawledMemorySnapshot resultPacked)
    {
        var theType = _checkNewTypes(nat, diffType);
        if (theType == null)
            return;
        string TypeName = MemUtil.GetGroupName(nat);
        string diffTypeName = MemUtil.GetCategoryLiteral(nat) + TypeName + diffType;
        var newNat=new NativeUnityEngineObject();
        newNat.caption = nat.caption;
        newNat.classID = nat.classID;
        newNat.className = TypeName + diffType;
        newNat.instanceID = nat.instanceID;
        newNat.isManager = false;
        newNat.size = nat.size;
        newNat.hideFlags = nat.hideFlags;
        newNat.isPersistent = nat.isPersistent;
        newNat.name = nat.name;
        newNat.referencedBy = nat.referencedBy;
        newNat.references = nat.references;
        newNat.isDontDestroyOnLoad = nat.isDontDestroyOnLoad;
        MemObject item = new MemObject(newNat, resultPacked);
        theType.AddObject(item);
    }

    void _handleDiffManangeObj(ManagedObject mao, string diffType, CrawledMemorySnapshot resultPacked)
    {
        var theType = _checkNewTypes(mao, diffType);
        if (theType == null)
            return;
        MemObject item = new MemObject(mao, resultPacked);
        string TypeName = MemUtil.GetGroupName(mao);

        var newMao = new ManagedObject();
        newMao.caption = TypeName + diffType;
        newMao.address = mao.address;
        newMao.referencedBy = mao.referencedBy;
        newMao.references = mao.references;
        newMao.size = mao.size;
        newMao.typeDescription = mao.typeDescription;
        //mao.caption = MemUtil.GetCategoryLiteral(mao) + TypeName + diffType;
        theType.AddObject(item);
    }

    MemType _checkNewTypes(ThingInMemory things, string diffType)
    {
        string TypeName = MemUtil.GetGroupName(things);
        if (TypeName.Length == 0 || things == null || TypeName.Contains(sDiffType.AdditiveType))
            return null;
        string diffTypeName = MemUtil.GetCategoryLiteral(things) + TypeName + diffType;
        MemType theType;
        if (!_types.ContainsKey(diffTypeName))
        {
            theType = new MemType();
            theType.TypeName = diffTypeName;
            theType.Category = MemUtil.GetCategory(things);
            theType.Objects = new List<object>();
            _types.Add(diffTypeName, theType);
        }
        else
        {
            theType = _types[diffTypeName];
        }
        return theType;
    }


    private Dictionary<object, Color> getSpecialColorDict(List<object> objs){
        Dictionary<object, Color> resultDict=  new Dictionary<object, Color>();
        foreach (object obj in objs)
        {
            var memType = obj as MemType;

            string typeName = memType.TypeName;
            if (typeName.Length == 0)
                continue;

            if (typeName.Contains(sDiffType.AdditiveType))
            {
                resultDict.Add(obj, Color.green);
            }
            else
                if (typeName.Contains(sDiffType.NegativeType))
                {
                    resultDict.Add(obj, Color.red);
                }
                else
                    if (typeName.Contains(sDiffType.ModificationType))
                    {
                        resultDict.Add(obj, Color.blue);
                    }
        }
        return resultDict;
    }

    public void RefreshTables()
    {
        if (_unpacked == null)
            return;

        List<object> qualified = new List<object>();

        // search for instances
        if (!string.IsNullOrEmpty(_searchInstanceString))
        {
            _types.Remove(MemConst.SearchResultTypeString);
            _searchResultType = new MemType();
            _searchResultType.TypeName = MemConst.SearchResultTypeString + " " + _searchInstanceString;
            _searchResultType.Category = 0;
            _searchResultType.Objects = new List<object>();

            string search = _searchInstanceString.ToLower();
            foreach (ThingInMemory thingInMemory in _unpacked.allObjects)
            {
                if (thingInMemory.caption.ToLower().Contains(search))
                {
                    _searchResultType.AddObject(new MemObject(thingInMemory, _unpacked));
                }
            }

            _types.Add(MemConst.SearchResultTypeString, _searchResultType);
            qualified.Add(_searchResultType);
            _typeTable.RefreshData(qualified,getSpecialColorDict(qualified));
            _objectTable.RefreshData(_searchResultType.Objects);
            return;
        }

        // search for types
        if (!string.IsNullOrEmpty(_searchTypeString))
        {
            foreach (var p in _types)
            {
                MemType mt = p.Value;
                if (mt.TypeName.ToLower().Contains(_searchTypeString.ToLower()))
                    qualified.Add(mt);
            }
            _typeTable.RefreshData(qualified,getSpecialColorDict(qualified));
            _objectTable.RefreshData(null);
            return;
        }

        // ordinary case - list categorized types and instances
        foreach (var p in _types)
        {
            MemType mt = p.Value;

            bool isAll = _memTypeCategory == 0;
            bool isNative = _memTypeCategory == 1 && mt.Category == 1;
            bool isManaged = _memTypeCategory == 2 && mt.Category == 2;
            bool isOthers = _memTypeCategory == 3 && (mt.Category == 3 || mt.Category == 4);
            if (isAll || isNative || isManaged || isOthers)
            {
                if (MemUtil.MatchSizeLimit(mt.Size, _memTypeSizeLimiter))
                {
                    qualified.Add(mt);
                }
            }
        }

        _typeTable.RefreshData(qualified,getSpecialColorDict(qualified));
        _objectTable.RefreshData(null);
    }

    public void Draw(Rect r)
    {
        int border = MemConst.TableBorder;
        float split = MemConst.SplitterRatio;
        int toolbarHeight = 50;

        GUILayout.BeginArea(r, MemStyles.Background);
        GUILayout.BeginHorizontal(MemStyles.Toolbar);

        // categories
        {
            GUILayout.Label("Category: ", GUILayout.MinWidth(120));

            string[] literals = _unpacked != null ? _categoryLiterals : MemConst.MemTypeCategories;

            int newCategory = GUILayout.SelectionGrid(_memTypeCategory, literals, literals.Length, MemStyles.ToolbarButton);
            if (newCategory != _memTypeCategory)
            {
                _memTypeCategory = newCategory;
                RefreshTables();
            }
        }
        GUILayout.FlexibleSpace();


        if (GUILayout.Button("DetailInfo", GUILayout.MinWidth(80)))
        {
            _staticDetailInfo.showInfos();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(3);

        GUILayout.BeginHorizontal(MemStyles.Toolbar);
        {
            string enteredString = GUILayout.TextField(_searchTypeString, 100, MemStyles.SearchTextField, GUILayout.MinWidth(200));
            if (enteredString != _searchTypeString)
            {
                _searchTypeString = enteredString;
                RefreshTables();
            }
            if (GUILayout.Button("", MemStyles.SearchCancelButton))
            {
                _types.Remove(MemConst.SearchResultTypeString);
                _searchTypeString = "";
                GUI.FocusControl(null); // Remove focus if cleared
                RefreshTables();
            }
        }

        // size limiter
        {
            GUILayout.Label("Size: ", GUILayout.MinWidth(120));
            int newLimiter = GUILayout.SelectionGrid(_memTypeSizeLimiter, MemConst.MemTypeLimitations, MemConst.MemTypeLimitations.Length, MemStyles.ToolbarButton);
            if (newLimiter != _memTypeSizeLimiter)
            {
                _memTypeSizeLimiter = newLimiter;
                RefreshTables();
            }
        }

        GUILayout.FlexibleSpace();

        // search box
        {
            string enteredString = GUILayout.TextField(_searchInstanceString, 100, MemStyles.SearchTextField, GUILayout.MinWidth(200));
            if (enteredString != _searchInstanceString)
            {
                _searchInstanceString = enteredString;
                RefreshTables();
            }
            if (GUILayout.Button("", MemStyles.SearchCancelButton))
            {
                _types.Remove(MemConst.SearchResultTypeString);
                _searchInstanceString = "";
                GUI.FocusControl(null); // Remove focus if cleared
                RefreshTables();
            }
        }

        GUILayout.EndHorizontal();

        int startY = toolbarHeight + border;
        int height = (int)(r.height - border * 2 - toolbarHeight);
        if (_typeTable != null)
            _typeTable.Draw(new Rect(border, startY, (int)(r.width * split - border * 1.5f), height));
        if (_objectTable != null)
            _objectTable.Draw(new Rect((int)(r.width * split + border * 0.5f), startY, (int)r.width * (1.0f - split) - border * 1.5f, height));
        GUILayout.EndArea();
    }

    void OnTypeSelected(object selected, int col)
    {
        MemType mt = selected as MemType;
        if (mt == null)
            return;

        _objectTable.RefreshData(mt.Objects);
    }

    void OnObjectSelected(object selected, int col)
    {
        var mpw = _hostWindow as MemoryProfilerWindow.MemoryProfilerWindow;
        if (mpw == null)
            return;

        var memObject = selected as MemObject;
        if (memObject == null)
            return;

        mpw.SelectThing(memObject._thing);
    }

    public void SelectThing(ThingInMemory thing)
    {
        if (_searchInstanceString == "")
        {
            string typeName = MemUtil.GetGroupName(thing);

            MemType mt;
            if (!_types.TryGetValue(typeName, out mt))
                return;

            if (_typeTable.GetSelected() != mt)
            {
                _typeTable.SetSelected(mt);
                _objectTable.RefreshData(mt.Objects);
            }

            foreach (var item in mt.Objects)
            {
                var mo = item as MemObject;
                if (mo != null && mo._thing == thing)
                {
                    if (_objectTable.GetSelected() != mo)
                    {
                        _objectTable.SetSelected(mo);
                    }
                    break;
                }
            }
        }
    }

    void OnDestroy()
    {
        if (_typeTable != null)
            _typeTable.Dispose();
        if (_objectTable != null)
            _objectTable.Dispose();

        _typeTable = null;
        _objectTable = null;
    }
}

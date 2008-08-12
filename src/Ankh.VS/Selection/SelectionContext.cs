﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using ShellConstants = Microsoft.VisualStudio.Shell.Interop.Constants;
using SharpSvn;

using Ankh.Scc;
using Ankh.Selection;
using Ankh.VS.SolutionExplorer;


namespace Ankh.VS.Selection
{
    /// <summary>
    /// 
    /// </summary>
    [GlobalService(typeof(ISelectionContext))]
    [GlobalService(typeof(ISccProjectWalker))]
    partial class SelectionContext : AnkhService, IVsSelectionEvents, IDisposable, ISelectionContext, ISelectionContextEx, ISccProjectWalker
    {
        IFileStatusCache _cache;
        SolutionExplorerWindow _solutionExplorer;
        bool _disposed;
        uint _cookie;

        uint _currentItem;
        IVsHierarchy _currentHierarchy;
        IVsMultiItemSelect _currentSelection;
        ISelectionContainer _currentContainer;
        IVsSolution _solution;

        CachedEnumerable<SelectionItem> _selectionItems;
        CachedEnumerable<SelectionItem> _selectionItemsRecursive;
        CachedEnumerable<string> _filenames;
        CachedEnumerable<string> _filenamesRecursive;
        CachedEnumerable<SvnItem> _svnItems;
        CachedEnumerable<SvnItem> _svnItemsRecursive;
        CachedEnumerable<SvnProject> _selectedProjects;
        CachedEnumerable<SvnProject> _selectedProjectsRecursive;
        Dictionary<Type, IEnumerable> _selectedItemsMap;
        IVsHierarchy _miscFiles;
        bool _deteminedSolutionExplorer;
        bool _isSolutionExplorer;
        bool? _isSolutionSelected;
        bool? _isSingleNodeSelection;
        string _solutionFilename;

        IVsHierarchy _filterHierarchy;
        uint _filterItem;

        public SelectionContext(IAnkhServiceProvider context)
            : base(context)
        {
        }

        protected override void OnInitialize()
        {
            _cache = GetService<IFileStatusCache>();
            _solutionExplorer = GetService<SolutionExplorerWindow>(typeof(IAnkhSolutionExplorerWindow));

            IVsMonitorSelection monitor = GetService<IVsMonitorSelection>();

            if (monitor != null)
                Marshal.ThrowExceptionForHR(monitor.AdviseSelectionEvents(this, out _cookie));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                IVsMonitorSelection monitor = (IVsMonitorSelection)Context.GetService(typeof(IVsMonitorSelection));

                if (_cookie != 0)
                    Marshal.ThrowExceptionForHR(monitor.UnadviseSelectionEvents(_cookie));
                ClearCache();
            }
        }

        #region IVsSelectionEvents Members

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive)
        {
            /// Some global state change which might change UI cueues
            return VSConstants.S_OK;
        }

        Disposer _disposer;
        Disposer Disposer
        {
            get { return _disposer ?? (_disposer = new Disposer()); }
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
                IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
        {
            if (!_disposed)
            {
                // The current selection changed; store for future reference
                _currentHierarchy = pHierNew;
                _currentItem = itemidNew;
                _currentSelection = pMISNew;
                _currentContainer = pSCNew;
            }

            if (_filterItem != VSConstants.VSITEMID_NIL)
            {
                if (_filterItem != _currentItem || _filterHierarchy != _currentHierarchy)
                {
                    // Clear the filter if the selection change is not to exactly the filtered item
                    _filterHierarchy = null;
                    _filterItem = VSConstants.VSITEMID_NIL;
                }
            }

            ClearCache();

            return VSConstants.S_OK;
        }

        private void ClearCache()
        {
            _selectionItems = null;
            _selectionItemsRecursive = null;
            _filenames = null;
            _filenamesRecursive = null;
            _svnItems = null;
            _svnItemsRecursive = null;
            _selectedProjects = null;
            _selectedProjectsRecursive = null;

            _deteminedSolutionExplorer = false;
            _isSolutionExplorer = false;
            _solutionFilename = null;
            _miscFiles = null;
            _selectedItemsMap = null;
            _isSolutionSelected = null;

            if (_disposer != null)
            {
                Disposer d = _disposer;
                _disposer = null;
                d.Dispose();
            }
        }

        public IVsHierarchy MiscellaneousProject
        {
            get { return _miscFiles ?? (_miscFiles = (VsShellUtilities.GetMiscellaneousProject(Context) as IVsHierarchy)); }
        }

        public IVsSolution Solution
        {
            get
            {
                if (_solution == null)
                    _solution = (IVsSolution)Context.GetService(typeof(SVsSolution));

                return _solution;
            }
        }

        public string SolutionFilename
        {
            get
            {
                if (_solutionFilename == null)
                {
                    if (Solution != null)
                    {
                        string solutionDir, solutionFile, solutionUserFile;
                        if (ErrorHandler.Succeeded(Solution.GetSolutionInfo(out solutionDir, out solutionFile, out solutionUserFile)))
                        {
                            _solutionFilename = solutionFile;
                        }
                    }
                }
                return _solutionFilename;
            }
        }

        #endregion

        internal sealed class SelectionItem : IEquatable<SelectionItem>
        {
            readonly IVsHierarchy _hierarchy;
            IVsSccProject2 _sccProject;
            readonly uint _id;
            bool _checkedSccProject;

            public SelectionItem(IVsHierarchy hierarchy, uint id)
            {
                if (hierarchy == null)
                    throw new ArgumentNullException("hierarchy");

                _hierarchy = hierarchy;
                _id = id;
            }

            public SelectionItem(IVsHierarchy hierarchy, uint id, IVsSccProject2 project)
                : this(hierarchy, id)
            {
                _sccProject = project;
            }

            public IVsHierarchy Hierarchy
            {
                [DebuggerStepThrough]
                get { return _hierarchy; }
            }

            public IVsSccProject2 SccProject
            {
                get
                {
                    if (_sccProject == null && !_checkedSccProject)
                    {
                        _checkedSccProject = true;
                        _sccProject = _hierarchy as IVsSccProject2;
                    }

                    return _sccProject;
                }
            }

            public uint Id
            {
                get { return _id; }
            }

            public bool IsSolution
            {
                get { return (SccProject != null) && SelectionUtils.IsSolutionSccProject(SccProject); }
            }

            #region IEquatable<SelectionItem> Members

            public bool Equals(SelectionItem other)
            {
                if (other == null)
                    return false;

                return (other.Id == Id) && (other.Hierarchy == Hierarchy);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as SelectionItem);
            }

            public override int GetHashCode()
            {
                return _hierarchy.GetHashCode() ^ _id.GetHashCode();
            }

            #endregion
        }

        protected bool MightBeSolutionExplorerSelection
        {
            get
            {
                if (!_deteminedSolutionExplorer && _solutionExplorer != null)
                {
                    _deteminedSolutionExplorer = true;
                    IVsUIHierarchyWindow hw = _solutionExplorer.HierarchyWindow;
                    IntPtr hierarchy;
                    IVsMultiItemSelect ms;
                    uint itemId;

                    if (!ErrorHandler.Succeeded(hw.GetCurrentSelection(out hierarchy, out itemId, out ms)))
                        return _isSolutionExplorer = false;

                    IVsHierarchy hier = null;
                    if (hierarchy != IntPtr.Zero)
                    {
                        hier = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierarchy);
                        Marshal.Release(hierarchy);
                    }

                    if (_currentItem != itemId)
                        return _isSolutionExplorer = false;

                    if (ms != _currentSelection)
                        return _isSolutionExplorer = false;

                    _isSolutionExplorer = ((hier is IVsSolution) || (hier == null)) && (SolutionFilename != null);
                }

                return _isSolutionExplorer;
            }
        }

        public bool IsSingleNodeSelection
        {
            get
            {
                if (!_isSingleNodeSelection.HasValue)
                {
                    if (_currentSelection != null)
                    {
                        uint nItems;
                        int withinSingleHierarchy;
                        if (ErrorHandler.Succeeded(_currentSelection.GetSelectionInfo(out nItems, out withinSingleHierarchy)))
                        {
                            if (nItems == 1)
                                _isSingleNodeSelection = true;
                            else
                                _isSingleNodeSelection = false;
                        }
                        else
                            _isSingleNodeSelection = true;
                    }
                    else if (_currentHierarchy != null)
                    {
                        switch (_currentItem)
                        {
                            case VSConstants.VSITEMID_SELECTION:
                            case VSConstants.VSITEMID_NIL:
                                _isSingleNodeSelection = false;
                                break;
                            default:
                                _isSingleNodeSelection = true;
                                break;
                        }
                    }
                    else
                        _isSingleNodeSelection = IsSolutionSelected;
                }

                return _isSingleNodeSelection.Value;
            }
        }

        protected IEnumerable<SelectionItem> GetSelectedItems(bool recursive)
        {
            return recursive ? GetSelectedItemsRecursive() : GetSelectedItems();
        }

        protected IEnumerable<SelectionItem> GetSelectedItems()
        {
            return _selectionItems ?? (_selectionItems = new CachedEnumerable<SelectionItem>(InternalGetSelectedItems(), Disposer));
        }

        protected IEnumerable<SelectionItem> GetSelectedItemsRecursive()
        {
            return _selectionItemsRecursive ?? (_selectionItemsRecursive = new CachedEnumerable<SelectionItem>(InternalGetSelectedItemsRecursive(), Disposer));
        }

        /// <summary>
        /// Gets the selected items; yielding for each result to allow delay loading
        /// </summary>
        /// <returns></returns>
        IEnumerable<SelectionItem> InternalGetSelectedItems()
        {
            if (_currentSelection != null)
            {
                uint nItems;
                int withinSingleHierarchy;

                if (!ErrorHandler.Succeeded(_currentSelection.GetSelectionInfo(out nItems, out withinSingleHierarchy)))
                    yield break;

                uint flags = 0;

                if ((withinSingleHierarchy != 0) && _currentHierarchy != null)
                    flags = (uint)__VSGSIFLAGS.GSI_fOmitHierPtrs; // Don't marshal the hierarchy for every item

                VSITEMSELECTION[] items = new VSITEMSELECTION[nItems];

                if (!ErrorHandler.Succeeded(_currentSelection.GetSelectedItems(flags, nItems, items)))
                    yield break;

                for (int i = 0; i < nItems; i++)
                {
                    IVsHierarchy hier = items[i].pHier ?? _currentHierarchy;

                    if (hier != null)
                        yield return new SelectionItem(hier, items[i].itemid);
                    else
                    {
                        if (items[i].itemid == VSConstants.VSITEMID_ROOT && MightBeSolutionExplorerSelection)
                            yield return new SelectionItem((IVsHierarchy)Solution, VSConstants.VSITEMID_ROOT,
                                SelectionUtils.GetSolutionAsSccProject(Context));
                        // else skip
                    }
                }
            }
            else if ((_currentHierarchy != null)
                && (_currentItem != VSConstants.VSITEMID_NIL)
                && (_currentItem != VSConstants.VSITEMID_SELECTION))
            {
                if (_currentItem == _filterItem && _currentHierarchy == _filterHierarchy)
                    yield break;

                yield return new SelectionItem(_currentHierarchy, _currentItem);
            }
            else if (_currentContainer == null)
            {
                // No selection, no hierarchy.... -> no selection!
            }
            else if (_currentItem == VSConstants.VSITEMID_ROOT)
            {
                // This is the case in the solution explorer when only the solution is selected

                // We must validate whether the window is really the solution explorer

                if (MightBeSolutionExplorerSelection)
                {
                    IVsHierarchy hier = (IVsHierarchy)Solution;

                    if (hier != null)
                        yield return new SelectionItem((IVsHierarchy)Solution, VSConstants.VSITEMID_ROOT,
                            SelectionUtils.GetSolutionAsSccProject(Context));
                }
            }
        }

        /// <summary>
        /// Gets the selected items and its descendants; yielding for each result to allow delay loading
        /// </summary>
        /// <returns></returns>
        IEnumerable<SelectionItem> InternalGetSelectedItemsRecursive()
        {
            Dictionary<SelectionItem, SelectionItem> ticked = new Dictionary<SelectionItem, SelectionItem>();
            foreach (SelectionItem si in GetSelectedItems())
            {
                if (ticked.ContainsKey(si))
                    continue;

                ticked.Add(si, si);
                yield return si;

                foreach (SelectionItem i in GetDescendants(si, ticked, ProjectWalkDepth.AllDescendants))
                {
                    yield return i;
                }
            }
        }

        static uint GetItemIdFromObject(object pvar)
        {
            if (pvar == null) return VSConstants.VSITEMID_NIL;
            if (pvar is int) return (uint)(int)pvar;
            if (pvar is uint) return (uint)pvar;
            if (pvar is short) return (uint)(short)pvar;
            if (pvar is ushort) return (uint)(ushort)pvar;
            if (pvar is long) return (uint)(long)pvar;
            return VSConstants.VSITEMID_NIL;
        }

        static Guid hierarchyId = typeof(IVsHierarchy).GUID;
        /// <summary>
        /// Gets the descendants of a selection item; yielding for each result to allow delay loading
        /// </summary>
        /// <param name="si"></param>
        /// <param name="previous"></param>
        /// <returns></returns>
        private IEnumerable<SelectionItem> GetDescendants(SelectionItem si, Dictionary<SelectionItem, SelectionItem> previous, ProjectWalkDepth depth)
        {
            if (si == null)
                throw new ArgumentNullException("si");

            // A hierarchy node can have 2 identities. We only need the inner one

            uint subId;
            IntPtr hierPtr;
            int hr = si.Hierarchy.GetNestedHierarchy(si.Id, ref hierarchyId, out hierPtr, out subId);

            if (ErrorHandler.Succeeded(hr) && hierPtr != IntPtr.Zero)
            {
                IVsHierarchy nestedHierarchy = Marshal.GetObjectForIUnknown(hierPtr) as IVsHierarchy;
                Marshal.Release(hierPtr);

                if (nestedHierarchy == null || (nestedHierarchy == MiscellaneousProject))
                    yield break;

                if (depth <= ProjectWalkDepth.AllDescendantsInHierarchy)
                {
                    yield break; // Don't walk into sub-hierarchies
                }
                else
                    si = new SelectionItem(nestedHierarchy, subId);
            }

            if (!previous.ContainsKey(si))
            {
                previous.Add(si, si);
                yield return si;
            }

            // Note: VS2005 and earlier have all projects on the top level; from VS2008+ projects are nested
            // We can ignore that as we would include the projects anyway

            object value;

            if (ErrorHandler.Succeeded(si.Hierarchy.GetProperty(si.Id,
                (int)__VSHPROPID.VSHPROPID_HasEnumerationSideEffects, out value)))
            {
                if (value != null)
                {
                    if ((bool)value)
                        yield break;
                }
            }

            if (!ErrorHandler.Succeeded(si.Hierarchy.GetProperty(si.Id,
                (int)__VSHPROPID.VSHPROPID_FirstChild, out value)))
            {
                yield break;
            }

            uint childId = GetItemIdFromObject(value);
            while (childId != VSConstants.VSITEMID_NIL)
            {
                SelectionItem i = new SelectionItem(si.Hierarchy, childId);

                foreach (SelectionItem ii in GetDescendants(i, previous, depth))
                {
                    yield return ii;
                }

                if (!ErrorHandler.Succeeded(si.Hierarchy.GetProperty(i.Id,
                    (int)__VSHPROPID.VSHPROPID_NextSibling, out value)))
                {
                    yield break;
                }

                childId = GetItemIdFromObject(value);
            }
        }


        #region ISelectionContext Members

        protected IEnumerable<string> GetSelectedFiles()
        {
            return _filenames ?? (_filenames = new CachedEnumerable<string>(InternalGetSelectedFiles(false), Disposer));
        }

        protected IEnumerable<string> GetSelectedFilesRecursive()
        {
            return _filenamesRecursive ?? (_filenamesRecursive = new CachedEnumerable<string>(InternalGetSelectedFiles(true), Disposer));
        }

        public IEnumerable<string> GetSelectedFiles(bool recursive)
        {
            return recursive ? GetSelectedFilesRecursive() : GetSelectedFiles();
        }

        /// <summary>
        /// Gets the selected files; yielding for each result to allow delay loading
        /// </summary>
        /// <param name="recursive"></param>
        /// <returns></returns>
        IEnumerable<string> InternalGetSelectedFiles(bool recursive)
        {
            HybridCollection<string> foundFiles = new HybridCollection<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SelectionItem i in GetSelectedItems(recursive))
            {
                string[] files;

                if (SelectionUtils.GetSccFiles(i, out files, true, true, null))
                {
                    foreach (string file in files)
                    {
                        if (!foundFiles.Contains(file))
                        {
                            foundFiles.Add(file);

                            yield return file;
                        }
                    }
                }
            }
        }

        protected IEnumerable<SvnItem> GetSelectedSvnItems()
        {
            return _svnItems ?? (_svnItems = new CachedEnumerable<SvnItem>(InternalGetSelectedSvnItems(false), Disposer));
        }

        protected IEnumerable<SvnItem> GetSelectedSvnItemsRecursive()
        {
            return _svnItemsRecursive ?? (_svnItemsRecursive = new CachedEnumerable<SvnItem>(InternalGetSelectedSvnItems(true), Disposer));
        }

        public IEnumerable<SvnItem> GetSelectedSvnItems(bool recursive)
        {
            return recursive ? GetSelectedSvnItemsRecursive() : GetSelectedSvnItems();
        }

        IEnumerable<SvnItem> InternalGetSelectedSvnItems(bool recursive)
        {
            if (_cache == null)
                yield break;

            foreach (string file in GetSelectedFiles(recursive))
            {
                yield return _cache[file];
            }
        }

        #endregion

        #region ISelectionContext Members

        protected IEnumerable<SvnProject> GetOwnerProjects()
        {
            return _selectedProjects ?? (_selectedProjects = new CachedEnumerable<SvnProject>(InternalGetOwnerProjects(false), Disposer));
        }

        protected IEnumerable<SvnProject> GetOwnerProjectsRecursive()
        {
            return _selectedProjectsRecursive ?? (_selectedProjectsRecursive = new CachedEnumerable<SvnProject>(InternalGetOwnerProjects(true), Disposer));
        }

        public IEnumerable<SvnProject> GetOwnerProjects(bool recursive)
        {
            return recursive ? GetOwnerProjectsRecursive() : GetOwnerProjects();
        }

        public IEnumerable<SvnProject> InternalGetOwnerProjects(bool recursive)
        {
            Hashtable ht = new Hashtable();
            bool searchedProjectMapper = false;
            IProjectFileMapper projectMapper = null;

            foreach (SelectionItem si in GetSelectedItems(recursive))
            {
                if (ht.Contains(si.Hierarchy))
                    continue;

                ht.Add(si.Hierarchy, si);

                if (si.SccProject != null)
                {
                    yield return new SvnProject(null, si.SccProject);
                    continue;
                }
                else if (si.Hierarchy is IVsSccVirtualFolders)
                    continue; // Skip URL WebApplications fast

                string[] files;

                // No need to fetch special files as we only want projects!
                if (!SelectionUtils.GetSccFiles(si, out files, false, false, null) || files.Length == 0)
                    continue; // No files selected

                if (projectMapper == null && !searchedProjectMapper)
                {
                    searchedProjectMapper = true;
                    projectMapper = Context.GetService<IProjectFileMapper>();
                }

                if (projectMapper != null)
                    foreach (string file in files)
                    {
                        foreach (SvnProject project in projectMapper.GetAllProjectsContaining(file))
                        {
                            if (project.RawHandle != null)
                            {
                                if (ht.Contains(project.RawHandle))
                                    continue;

                                ht.Add(project.RawHandle, si);
                                yield return project;
                            }
                            else if (!ht.Contains(project))
                            {
                                ht.Add(project, si);
                                yield return project;
                            }
                        }
                    }
            }
        }


        #endregion

        #region ISccProjectWalker Members

        /// <summary>
        /// Gets the list of files specified by the hierarchy (IVsSccProject2 or IVsHierarchy)
        /// </summary>
        /// <param name="hierarchy"></param>
        /// <param name="id"></param>
        /// <param name="depth"></param>
        /// <returns></returns>
        /// <remarks>The list might contain duplicates if files are included more than once</remarks>
        public IEnumerable<string> GetSccFiles(IVsHierarchy hierarchy, uint id, ProjectWalkDepth depth, IDictionary<string, uint> map)
        {
            // Note: This command is not cached like the other commands on this object!
            if (hierarchy == null)
                throw new ArgumentNullException("hierarchy");

            SelectionItem si = new SelectionItem(hierarchy, id);

            string[] files;
            if (!SelectionUtils.GetSccFiles(si, out files, depth >= ProjectWalkDepth.SpecialFiles, depth != ProjectWalkDepth.AllDescendantsInHierarchy, map))
                yield break;

            foreach (string file in files)
            {
                yield return SvnTools.GetNormalizedFullPath(file);
            }

            if (depth > ProjectWalkDepth.SpecialFiles)
            {
                Dictionary<SelectionItem, SelectionItem> previous = new Dictionary<SelectionItem, SelectionItem>();
                previous.Add(si, si);

                foreach (SelectionItem item in GetDescendants(si, previous, depth))
                {
                    if (!SelectionUtils.GetSccFiles(item, out files, depth >= ProjectWalkDepth.SpecialFiles, depth != ProjectWalkDepth.AllDescendantsInHierarchy, map))
                        continue;

                    foreach (string file in files)
                    {
                        yield return SvnTools.GetNormalizedFullPath(file);
                    }
                }
            }
        }

        void ISccProjectWalker.SetPrecreatedFilterItem(IVsHierarchy hierarchy, uint id)
        {
            if (id != VSConstants.VSITEMID_NIL || _filterItem != VSConstants.VSITEMID_NIL)
                ClearCache(); // Make sure we use the filter directly

            _filterHierarchy = hierarchy;
            _filterItem = id;
        }

        #endregion

        #region ISelectionContext Members

        public IEnumerable<SvnProject> GetSelectedProjects(bool recursive)
        {
            foreach (SelectionItem item in GetSelectedItems(recursive))
            {
                if (item.Id == VSConstants.VSITEMID_ROOT)
                {
                    if (!item.IsSolution && item.SccProject != null)
                        yield return new SvnProject(null, item.SccProject);
                }
            }
        }

        public bool IsSolutionSelected
        {
            get
            {
                if (_isSolutionSelected.HasValue)
                    return _isSolutionSelected.Value;

                foreach (SelectionItem item in GetSelectedItems(false))
                {
                    if (item.IsSolution)
                    {
                        _isSolutionSelected = true;
                        return true;
                    }
                }

                _isSolutionSelected = false;
                return false;
            }
        }

        public IEnumerable<T> GetSelection<T>()
            where T : class
        {
            IEnumerable enumerable;
            if (_selectedItemsMap != null && _selectedItemsMap.TryGetValue(typeof(T), out enumerable))
                return (IEnumerable<T>)enumerable;

            IEnumerable<T> v = new CachedEnumerable<T>(InternalGetSelection<T>(), Disposer);

            if (_selectedItemsMap == null)
                _selectedItemsMap = new Dictionary<Type, IEnumerable>();

            _selectedItemsMap.Add(typeof(T), v);
            return v;
        }

        IEnumerable<T> InternalGetSelection<T>()
            where T : class
        {
            ISelectionContainer sc = _currentContainer;

            uint nItems;
            if (sc == null || !ErrorHandler.Succeeded(sc.CountObjects((uint)ShellConstants.GETOBJS_SELECTED, out nItems)))
                yield break;

            object[] objs = new object[(int)nItems];
            if (ErrorHandler.Succeeded(sc.GetObjects((uint)ShellConstants.GETOBJS_SELECTED, nItems, objs)))
            {
                foreach (object o in objs)
                {
                    T i = o as T;

                    if (i != null)
                        yield return i;
                }
            }
        }
        #endregion
    }
}

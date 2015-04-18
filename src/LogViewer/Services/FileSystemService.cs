﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="FileSystemService.cs" company="Wild Gums">
//   Copyright (c) 2008 - 2015 Wild Gums. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace LogViewer.Services
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Catel;
    using Catel.Collections;
    using Catel.Logging;
    using Catel.Services;
    using Models;

    internal class FileSystemService : IFileSystemService
    {
        #region Fields
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();
        private readonly IDispatcherService _dispatcherService;
        private readonly IFileNodeService _fileNodeService;
        private readonly IFileSystemWatchingService _fileSystemWatchingService;
        private readonly IFilterService _filterService;
        private readonly IFileBrowserService _fileBrowserService;
        private readonly INavigationNodeCacheService _navigationNodeCacheService;
        private string _regexFilter;
        private string _wildcardsFilter;
        #endregion

        #region Constructors
        public FileSystemService(IDispatcherService dispatcherService, IFileNodeService fileNodeService, IFileSystemWatchingService fileSystemWatchingService,
            INavigationNodeCacheService navigationNodeCacheService, IFilterService filterService, IFileBrowserService fileBrowserService)
        {
            Argument.IsNotNull(() => dispatcherService);
            Argument.IsNotNull(() => fileNodeService);
            Argument.IsNotNull(() => fileSystemWatchingService);
            Argument.IsNotNull(() => navigationNodeCacheService);
            Argument.IsNotNull(() => filterService);
            Argument.IsNotNull(() => fileBrowserService);

            _dispatcherService = dispatcherService;
            _fileNodeService = fileNodeService;
            _fileSystemWatchingService = fileSystemWatchingService;
            _navigationNodeCacheService = navigationNodeCacheService;
            _filterService = filterService;
            _fileBrowserService = fileBrowserService;

            Filter = "*.log";

            fileSystemWatchingService.ContentChanged += OnFolderContentChanged;
        }
        #endregion

        #region Properties
        public string Filter
        {
            get { return _wildcardsFilter; }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _wildcardsFilter = string.Empty;
                    _regexFilter = string.Empty;
                }
                else
                {
                    _wildcardsFilter = value;
                    _regexFilter = _wildcardsFilter.ConvertWildcardToRegex();
                    _fileSystemWatchingService.UpdateFilter(_wildcardsFilter);
                }
            }
        }
        #endregion

        #region Methods
        public FolderNode LoadFileSystemContent(string path, bool isNavigationRoot = false)
        {
            Argument.IsNotNullOrEmpty(() => path);

            Log.Debug("Loading file system content '{0}'", path);

            var directoryInfo = new DirectoryInfo(path);

            FolderNode folder = null;
            _dispatcherService.Invoke(() => { folder = new FolderNode(directoryInfo); });

            var logFiles = Directory.GetFiles(path, _wildcardsFilter, SearchOption.TopDirectoryOnly).Where(x => x.IsSupportedFile(_regexFilter)).OrderBy(x => new FileInfo(x).Name).ToList();

            var fileNodes = new List<FileNode>();

#if LOADFILES_PARALLEL
            var fileTasks = new List<Action>();

            foreach (var logFile in logFiles)
            {
                var file = logFile;

                fileTasks.Add(() =>
                {
                    var fileNode = LoadFileFromFileSystem(Path.Combine(path, file));

                    lock (fileTasks)
                    {
                        fileNodes.AddDescendingly(fileNode, CompareFileNodes);
                    }
                });
            }

            // Parse all files parallel
            TaskHelper.RunAndWait(fileTasks.ToArray());
#else
            foreach (var logFile in logFiles)
            {
                var fileNode = LoadFileFromFileSystem(Path.Combine(path, logFile));
                fileNodes.AddDescendingly(fileNode, CompareFileNodes);
            }
#endif

            _dispatcherService.Invoke(() => folder.Files = new ObservableCollection<FileNode>(fileNodes));

            var logDirectories = Directory.GetDirectories(path).Select(x => Path.Combine(path, x));

#if LOADDIRECTORIES_PARALLEL
            var directoryTasks = new List<Action>();

            foreach (var directory in logDirectories)
            {
                var dir = directory;

                directoryTasks.Add(() =>
                {
                    var folderNode = LoadFileSystemContent(Path.Combine(path, dir));
                    _dispatcherService.Invoke(() => folder.Directories.Add(folderNode));
                });
            }

            // Parse all directories parallel
            TaskHelper.RunAndWait(directoryTasks.ToArray());
#else
            foreach (var directory in logDirectories)
            {
                var fileSystemContent = LoadFileSystemContent(directory);
                _dispatcherService.Invoke(() => folder.Directories.Add(fileSystemContent));
            }
#endif

            if (isNavigationRoot)
            {
                _fileSystemWatchingService.BeginDirectoryWatching(folder.FullName, _wildcardsFilter);
            }
            else
            {
                folder.UpdateVisibility();
            }

            _navigationNodeCacheService.AddToCache(folder);

            return folder;
        }

        public void ReleaseFileSystemContent(FolderNode folder)
        {
            Argument.IsNotNull(() => folder);

            _fileSystemWatchingService.EndDirectoryWatching(folder.FullName);
            OnDeleted(folder.FullName);
        }

        private async void OnRenamed(string newName, string oldName)
        {
            Argument.IsNotNullOrEmpty(() => oldName);
            Argument.IsNotNullOrEmpty(() => newName);

            if (newName.IsFile())
            {
                await RenameFile(oldName, newName);
            }

            if (newName.IsDirectory())
            {
                await RenameFolder(oldName, newName);
            }

            _filterService.ApplyFilesFilter();
        }

        private void OnCreated(string fullPath)
        {
            Argument.IsNotNullOrEmpty(() => fullPath);

            var folder = GetParentFolderNode(fullPath);

            if (folder == null)
            {
                OnCreated(Catel.IO.Path.GetParentDirectory(fullPath));
                return;
            }

            if (fullPath.IsFile() && folder.Files.FirstOrDefault(x => string.Equals(x.FullName, fullPath)) == null)
            {
                if (fullPath.IsSupportedFile(_regexFilter))
                {
                    var fileNode = GetFromCacheOrLoad(fullPath);
                    folder.Files.AddDescendingly(fileNode, CompareFileNodes);
                    _navigationNodeCacheService.AddToCache(fileNode);
                }
            }

            if (fullPath.IsDirectory())
            {
                var folderNode = LoadFileSystemContent(fullPath);
                folderNode.IsVisible = false;
                folder.Directories.Add(folderNode);
                _navigationNodeCacheService.AddToCache(folderNode);
            }

            _filterService.ApplyFilesFilter();
        }

        private int CompareFileNodes(FileNode fileNode1, FileNode fileNode2)
        {
            Argument.IsNotNull(() => fileNode1);
            Argument.IsNotNull(() => fileNode2);

            var name1 = fileNode1.Name;
            var name2 = fileNode2.Name;

            return string.Compare(name1, 0, name2, 0, Math.Min(name1.Length, name2.Length));
        }

        private void OnDeleted(string fullPath)
        {
            Argument.IsNotNullOrEmpty(() => fullPath);

            var folder = GetParentFolderNode(fullPath);

            if (folder != null)
            {
                folder.Directories.RemoveByPredicate(x => string.Equals(x.FullName, fullPath));
                folder.Files.RemoveByPredicate(x => string.Equals(x.FullName, fullPath));                
            }
            else
            {
                var rootDirectories = _fileBrowserService.FileBrowserModel.RootDirectories;
                rootDirectories.RemoveByPredicate(x => string.Equals(x.FullName, fullPath));
            }

            _navigationNodeCacheService.RemoveFromCache(fullPath);
            _filterService.ApplyFilesFilter();
        }

        private void OnChanged(string fullPath)
        {
            Argument.IsNotNullOrEmpty(() => fullPath);

            var fileNode = GetFromCacheOrLoad(fullPath);
            if (fileNode.IsItemSelected)
            {
                _fileNodeService.ParallelLoadFileNodeBatch(fileNode);
            }

            //_filterService.ApplyLogRecordsFilter(fileNode);
        }

        private FileNode GetFromCacheOrLoad(string fullPath)
        {
            Argument.IsNotNullOrEmpty(() => fullPath);

            var fileNode = _navigationNodeCacheService.GetFromCache<FileNode>(fullPath);
            if (fileNode == null)
            {
                fileNode = LoadFileFromFileSystem(fullPath);
            }

            return fileNode;
        }

        private async Task RenameFolder(string oldName, string newName)
        {
            Argument.IsNotNullOrEmpty(() => oldName);
            Argument.IsNotNullOrEmpty(() => newName);

            var fromCache = _navigationNodeCacheService.GetFromCache<FolderNode>(newName);
            if (fromCache != null)
            {
                return;
            }

            var folder = GetParentFolderNode(newName);

            var oldDir = folder.Directories.FirstOrDefault(x => string.Equals(x.FullName, oldName));
            if (oldDir == null)
            {
                return;
            }

            folder.Directories.Remove(oldDir);
            _navigationNodeCacheService.RemoveFromCache(oldName);

            ClearSubfolders(oldDir);

            if (Directory.Exists(newName))
            {
                var newDir = LoadFileSystemContent(newName);
                folder.Directories.Add(newDir);
                _navigationNodeCacheService.AddToCache(newDir);
            }
        }

        private void ClearSubfolders(FolderNode folder)
        {
            Argument.IsNotNull(() => folder);

            foreach (var folderNode in folder.Directories)
            {
                ClearSubfolders(folderNode);
            }

            folder.Directories.Clear();
        }

        private async Task RenameFile(string oldName, string newName)
        {
            Argument.IsNotNullOrEmpty(() => oldName);
            Argument.IsNotNullOrEmpty(() => newName);

            var fromCache = _navigationNodeCacheService.GetFromCache<FileNode>(newName);
            if (fromCache != null)
            {
                return;
            }

            var folder = GetParentFolderNode(newName);

            if (folder == null)
            {
                OnCreated(Catel.IO.Path.GetParentDirectory(newName));
                return;
            }

            var fileNode = folder.Files.FirstOrDefault(x => string.Equals(x.FullName, oldName));
            if (fileNode == null)
            {
                OnCreated(newName);
                return;
            }

            if (!newName.IsSupportedFile(_regexFilter))
            {
                folder.Files.Remove(fileNode);
                _navigationNodeCacheService.RemoveFromCache(fileNode.FullName);
                return;
            }

            folder.Files.Remove(fileNode);
            _navigationNodeCacheService.RemoveFromCache(fileNode.FullName);
            fileNode.FileInfo = new FileInfo(newName);
            folder.Files.AddDescendingly(fileNode, CompareFileNodes);
            _navigationNodeCacheService.AddToCache(fileNode);
        }

        private void OnFolderContentChanged(object sender, FolderNodeEventArgs e)
        {
            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Changed:
                    _dispatcherService.BeginInvoke(() => OnChanged(e.NewPath));
                    break;

                case WatcherChangeTypes.Created:
                    _dispatcherService.BeginInvoke(() => OnCreated(e.NewPath));
                    break;

                case WatcherChangeTypes.Deleted:
                    _dispatcherService.BeginInvoke(() => OnDeleted(e.OldPath));
                    break;

                case WatcherChangeTypes.Renamed:
                    _dispatcherService.BeginInvoke(() => OnRenamed(e.NewPath, e.OldPath));
                    break;
            }
        }

        private FileNode LoadFileFromFileSystem(string fullName)
        {
            Argument.IsNotNullOrEmpty(() => fullName);

            var fileNode = _fileNodeService.CreateFileNode(fullName);

            _navigationNodeCacheService.AddToCache(fileNode);

            return fileNode;
        }

        private FolderNode GetParentFolderNode(string fullPath)
        {
            Argument.IsNotNullOrEmpty(() => fullPath);

            var parentDirectory = Catel.IO.Path.GetParentDirectory(fullPath);
            var folder = _navigationNodeCacheService.GetFromCache<FolderNode>(parentDirectory);
            return folder;
        }
        #endregion
    }
}
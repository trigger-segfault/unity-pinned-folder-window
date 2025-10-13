#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TriggerSegfault.editor
{
    // A mini file list intended for use where taking out assets from a single
    // folder is common, but the Project Browser keeps attempting to navigate
    // away (for all sorts of dumb reasons).
    [EditorWindowTitle(title = DefaultTitle, icon = "Project")]
    public class PinnedFolderWindow : EditorWindow
    {
        // Fallback to this title if no folder path is selected.
        const string DefaultTitle = "Folder";

        const string MenuFolder = "Trigger Segfault/";
        const string OpenNewMenuName = "New Pinned Folder";
        const string WindowOpenNewMenuPath = "Window/" + MenuFolder + OpenNewMenuName;
        const string AssetsOpenNewMenuPath = "Assets/" + MenuFolder + OpenNewMenuName;
        const string AssetsOpenExistingMenuPath = "Assets/" + MenuFolder + "Existing Pinned Folder";
        // 1101 is just below lilToon's submenu (which is the farthest for me
        // before the "Properties..." separator).
        const int AssetsPriority = 1101;

        private class AssetReference
        {
            // Link to the asset used for persistent identification between
            // re-serialization, domain reloads, etc.
            public string GUID;
            //public UnityEngine.Object AssetObject;

            public bool IsFolder;
            // Determines if an empty folder icon should be drawn.
            public bool IsEmptyFolder;
            public string FilePath;

            [NonSerialized]
            private string m_cachedFileName;
            public string FileName
                => (m_cachedFileName ??= Path.GetFileNameWithoutExtension(FilePath));

            [NonSerialized]
            private UnityEngine.Object m_cachedAssetObject;
            public UnityEngine.Object AssetObject
            {
                get
                {
                    if (m_cachedAssetObject == null)
                    {
                        m_cachedAssetObject = AssetDatabase.LoadMainAssetAtPath(FilePath);
                    }
                    return m_cachedAssetObject;
                }
            }

            public Texture2D Icon
            {
                get
                {
                    var icon = AssetDatabase.GetCachedIcon(FilePath) as Texture2D;
                    if (icon != null)
                    {
                        return icon;
                    }
                    // Icon may not be cached yet.
                    icon = AssetPreview.GetMiniThumbnail(AssetObject);
                    if (icon != null)
                    {
                        return icon;
                    }
                    // This probably shouldn't happen, but handle it to ensure
                    // that file paths stay aligned with all other items.
                    return Styles.TransparentIcon;
                }
            }

            // public class GUIDEqualityComparer : IEqualityComparer<AssetReference>
            // {
            //     public bool Equals(AssetReference x, AssetReference y)
            //         => x.GUID == y.GUID;

            //     public int GetHashCode(AssetReference obj)
            //         => obj.GUID.GetHashCode();
            // }
        }

        private string m_path = null;
        // Don't serialize, because this contains object references. Instead,
        // rebuild this list during OnEnable().
        [NonSerialized]
        private AssetReference[] m_assets = Array.Empty<AssetReference>();

        private Vector2 m_scrollPos = Vector2.zero;
        [NonSerialized]
        private int m_selectedIndex = -1;
        // Used to restore file selection during SetFolderPath(restore: true).
        private string m_selectedGUID = null;
        // If ScrollToIndex() is called outside of DrawList(). Such as after a
        // drop during DrawTopBar() and after SetFolderPath(restore: true).
        [NonSerialized]
        private bool m_needsScrollToIndex = false;

        // Only valid during OnGUI():
        [NonSerialized]
        private int m_topBarKeyboardID = 0;
        [NonSerialized]
        private int m_listKeyboardID = 0;

        // Only valid during and after DrawList():
        [NonSerialized]
        private float m_listMaxWidth = 0f;
        [NonSerialized]
        private Rect m_listRect = Rect.zero;

        // DragDelay:
        [NonSerialized]
        private bool m_inGUI = false;
        [NonSerialized]
        private int m_dragIndex = -1;
        [NonSerialized]
        private Vector2 m_dragStartPosition;

        // Future customization:
        private static readonly bool ShowTopBar = true;

        private AssetReference SelectedAsset
            => (m_selectedIndex != -1 ? m_assets[m_selectedIndex] : null);

        private static AssetReference[] LoadAssets(string folderPath)
        {
            // FindAssets() returns items in subfolders as well, this filters
            // out any files not in the target folder.
            bool IsTopLevelFile(string filePath)
                => filePath.LastIndexOf('/') <= folderPath.Length;

            static bool IsFolderEmpty(string filePath)
                => AssetDatabase.FindAssets(null, new string[] { filePath }).Length == 0;

            AssetReference AssetReferenceFromGUID(string guid)
            {
                try
                {
                    string filePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (filePath != null && IsTopLevelFile(filePath))
                    {
                        var assetObject = AssetDatabase.LoadMainAssetAtPath(filePath);
                        if (assetObject != null)
                        {
                            bool isFolder = AssetDatabase.IsValidFolder(filePath);
                            return new AssetReference
                            {
                                GUID = guid,
                                FilePath = filePath,
                                IsFolder = isFolder,
                                IsEmptyFolder = isFolder && IsFolderEmpty(filePath),
                            };
                        }
                    }
                }
                catch {}
                return null;
            }

            var assets = AssetDatabase.FindAssets(null, new string[] { folderPath })
                        .Select(AssetReferenceFromGUID)
                        // AssetReferenceFromGUID() returns null if anything
                        // fails, or if IsTopLevelFile() returns false.
                        .Where(a => a != null)
                        .ToList();

            // Move folders to the top of the list.
            int folderCount = 0;
            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].IsFolder)
                {
                    // Check if we've already encountered files, meaning this
                    // folder needs to be moved up.
                    if (folderCount < i)
                    {
                        var asset = assets[i];
                        assets.RemoveAt(i);
                        assets.Insert(folderCount, asset);
                    }
                    folderCount++;
                }
            }

            return assets.ToArray();
        }

        private void RestoreFolderPath()
        {
            if (m_path != null)
            {
                SetFolderPath(m_path, restore: true);
            }
        }

        public void SetFolderPath(string newPath, bool restore = false)
        {
            string selectedFilePath = null;
            if (newPath != null)
            {
                // Opening from a file will use the file's folder.
                if (!AssetDatabase.IsValidFolder(newPath))
                {
                    if (TryGetValidParentFolder(newPath, out string parentDir))
                    {
                        selectedFilePath = newPath;
                        newPath = parentDir;
                    }
                    else
                    {
                        // Not a valid folder, default to no folder.
                        selectedFilePath = null;
                        newPath = null;
                        restore = false;
                    }
                }

                // Somewhat normalize path.
                newPath = NormalizePath(newPath);
            }

            CancelDragDelay();
            if (restore || m_path != newPath)
            {
                // Reset scroll position if the path has changed.
                if (m_path != newPath)
                {
                    m_scrollPos = Vector2.zero;
                }

                if (newPath != null)
                {
                    m_assets = LoadAssets(newPath);
                    if (selectedFilePath == null && restore)
                    {
                        // Restore file selection.
                        SelectFileGUID(m_selectedGUID, scrollIntoView: false);
                    }
                    else
                    {
                        SelectFileIndex(-1, scrollIntoView: false);
                    }
                    // Distinguish title from Project Browser window (if folder
                    // is named "Project") by appending a trailing slash.
                    titleContent.text = Path.GetFileName(newPath) + "/";
                    titleContent.tooltip = newPath;
                }
                else
                {
                    m_assets = Array.Empty<AssetReference>();
                    SelectFileIndex(-1, scrollIntoView: false);
                    titleContent.text = DefaultTitle;
                    titleContent.tooltip = null;
                }
                m_path = newPath;
                // Repaint because the entire list has changed. (Duh)
                Repaint();
            }
            if (newPath != null && selectedFilePath != null)
            {
                // Support for selecting file if path was not a folder.
                SelectFilePath(selectedFilePath, scrollIntoView: true);
            }
        }

        private void SelectFilePath(string filePath, bool scrollIntoView)
        {
            SelectFileGUID(
                AssetDatabase.GUIDFromAssetPath(filePath).ToString(),
                scrollIntoView
            );
        }

        private void SelectFileGUID(string guid, bool scrollIntoView)
        {
            SelectFileIndex(
                Array.FindIndex(m_assets, a => a.GUID == guid),
                scrollIntoView
            );
        }

        private void SelectFileIndex(int index, bool scrollIntoView)
        {
            if (index < -1 || index >= m_assets.Length)
            {
                index = -1;
            }
            if (m_selectedIndex != index)
            {
                // Repaint if the selected index has changed. Note that
                // ScrollToIndex() will initiate its own repaint if
                // scrollIntoView is true, but it won't do so if we haven't
                // performed a layout event yet.
                Repaint();
            }

            m_needsScrollToIndex = false;
            m_selectedIndex = index;
            m_selectedGUID = (index != -1 ? m_assets[index].GUID : null);

            if (scrollIntoView)
            {
                ScrollToIndex(m_selectedIndex);
            }
        }

        private void ScrollToIndex(int index)
        {
            if (m_listRect == Rect.zero)
            {
                // Layout not performed yet. Can't determine scroll position.
                m_needsScrollToIndex = true;
            }
            else
            {
                m_needsScrollToIndex = false;

                float yMin = Math.Max(0, index) * Styles.ItemHeight;
                float yMax = yMin + Styles.ItemHeight;

                float height = m_listRect.height;
                if (yMax > m_scrollPos.y + height)
                {
                    m_scrollPos.y = yMax - height;
                }
                if (yMin < m_scrollPos.y)
                {
                    m_scrollPos.y = yMin;
                }

                m_scrollPos.y = Math.Max(0f, m_scrollPos.y);
                // Repaint to update the scroll view position.
                Repaint();
            }
        }

        // private bool AssetsEqual(AssetReference[] assets)
        // {
        //     // return m_assets.Length == assets.Length && m_assets.SequenceEqual(
        //     //         assets, new AssetReference.GUIDEqualityComparer()
        //     //     );
        //     if (m_assets.Length != assets.Length)
        //     {
        //         return false;
        //     }
        //     else
        //     {
        //         for (int i = 0; i < m_assets.Length; i++)
        //         {
        //             if (m_assets[i].GUID != assets[i].GUID)
        //             {
        //                 return false;
        //             }
        //         }
        //         return true;
        //     }
        // }

        #region OnGUI and Events
        [MenuItem(WindowOpenNewMenuPath, false)]
        [MenuItem(AssetsOpenNewMenuPath, false, AssetsPriority)]
        static void ShowNewWindow() => ShowWindow(createNew: true);

        [MenuItem(AssetsOpenExistingMenuPath, false, AssetsPriority)]
        static void ShowExistingWindow() => ShowWindow(createNew: false);

        [MenuItem(AssetsOpenExistingMenuPath, true)]
        static bool ValidateShowExistingWindow()
        {
            return EditorWindow.HasOpenInstances<PinnedFolderWindow>();
        }

        private static void ShowWindow(bool createNew)
        {
            string guid = Selection.assetGUIDs.FirstOrDefault();
            var window = createNew
                ? EditorWindow.CreateWindow<PinnedFolderWindow>(
                    desiredDockNextTo: new Type[] { typeof(PinnedFolderWindow) }
                )
                : EditorWindow.GetWindow<PinnedFolderWindow>();
            if (guid != null)
            {
                window.SetFolderPath(AssetDatabase.GUIDToAssetPath(guid));
            }
        }

        void OnEnable()
        {
            // Reload current path on editor/domain reload.
            RestoreFolderPath();
        }

        void OnDisable()
        {
            CancelDragDelay();
        }

        void OnProjectChange()
        {
            // Ensure changes made to project files are reflected in file list.
            RestoreFolderPath();
        }

        void OnGUI()
        {
            // Styles must be initialized in OnGUI() or later, this way we can
            // ensure the needed resources are ready to use.
            InitializeStyles();

            m_inGUI = true;
            try
            {
                m_topBarKeyboardID = GUIUtility.GetControlID(FocusType.Keyboard);
                m_listKeyboardID = GUIUtility.GetControlID(FocusType.Keyboard);

                // If this window is focused, but nothing has keyboard focus,
                // give focus to the file list control. Note that trying to use
                // OnFocus() as a refocus check does not work, since that
                // requires keyboard focus itself.
                if (EditorWindow.focusedWindow == this &&
                    GUIUtility.keyboardControl != m_topBarKeyboardID &&
                    GUIUtility.keyboardControl != m_listKeyboardID)
                {
                    GUIUtility.keyboardControl = m_listKeyboardID;

                    // Repaint to change the list selection color from inactive
                    // to active.
                    Repaint();
                }

                if (m_needsScrollToIndex)
                {
                    ScrollToIndex(m_selectedIndex);
                }

                DrawTopBar();

                float yStart = GUILayoutUtility.GetLastRect().yMax;

                m_scrollPos =
                    EditorGUILayout.BeginScrollView(m_scrollPos, Styles.AreaBg);
                try
                {
                    DrawList(yStart);
                }
                finally
                {
                    EditorGUILayout.EndScrollView();
                }
            }
            finally
            {
                m_inGUI = false;
            }
        }

        private void DrawTopBar()
        {
            static bool IsValidFolderDrop(out string path)
            {
                var paths = DragAndDrop.paths;
                if (paths.Length == 1 && (
                    AssetDatabase.IsValidFolder(paths[0]) ||
                    TryGetValidParentFolder(paths[0], out _)))
                {
                    path = paths[0];
                }
                else
                {
                    path = null;
                }
                return path != null;
            }

            GUIStyle style = Styles.TopBar;
            float height = (ShowTopBar ? Styles.TopBarHeight : 0f);
            Rect rect = EditorGUILayout.GetControlRect(false, height, style);

            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(m_topBarKeyboardID);

            if (!ShowTopBar)
            {
                switch (eventType)
                {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    // Cancel active drop if the top bar was hidden.
                    if (DragAndDrop.activeControlID == m_topBarKeyboardID)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.None;
                        DragAndDrop.activeControlID = 0;
                    }
                    break;
                }
                return;
            }

            switch (eventType)
            {
            case EventType.DragUpdated:
            case EventType.DragPerform:
                if (rect.Contains(e.mousePosition) &&
                    IsValidFolderDrop(out string path))
                {
                    if (DragAndDrop.visualMode == DragAndDropVisualMode.None)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                    }
                    e.Use();
                    if (eventType == EventType.DragUpdated)
                    {
                        DragAndDrop.activeControlID = m_topBarKeyboardID;
                        // Repaint to highlight the top bar text to show that a
                        // drop is valid.
                        Repaint();
                    }
                    else
                    {
                        // Change the folder path to that of the file dragged
                        // onto the top bar. If the path was a file and not a
                        // folder, then use the parent folder and select the
                        // file.
                        DragAndDrop.AcceptDrag();
                        DragAndDrop.activeControlID = 0;
                        // Repaint to show that a drop is no longer occurring.
                        // SetFolderPath() is not required to repaint if the
                        // path wasn't changed.
                        Repaint();
                        SetFolderPath(path);
                        GUIUtility.ExitGUI();
                    }
                }
                break;
            case EventType.Repaint:
                GUIContent content = TempContent(m_path ?? "No folder selected...");
                style.Draw(
                    rect,
                    content,
                    controlID: m_topBarKeyboardID,
                    on: DragAndDrop.activeControlID == m_topBarKeyboardID,
                    hover: rect.Contains(e.mousePosition)
                );
                break;
            }
        }

        private void DrawList(float yStart)
        {
            Vector2 origIconSize = EditorGUIUtility.GetIconSize();
            try
            {
                Event e = Event.current;
                EventType eventType = e.GetTypeForControl(m_listKeyboardID);

                if (eventType == EventType.Layout || eventType == EventType.Repaint)
                {
                    // We're either laying out, and need the icon dimensions
                    // for sizing, or need the icon dimensions for repainting.
                    EditorGUIUtility.SetIconSize(Styles.IconSize);
                }

                // Measure max width so that we can show horizontal scrollbar.
                // Layout is always the first event, so no need to check if the
                // cached width is assigned.
                if (eventType == EventType.Layout)
                {
                    float maxWidth = 0f;
                    foreach (var asset in m_assets)
                    {
                        float width = Styles.ListItem.CalcSize(TempContent(
                            asset.FileName,
                            // Use dummy texture, since all we need is the area
                            // consumed by the fixed-size icon.
                            EditorGUIUtility.whiteTexture
                        )).x;
                        maxWidth = Math.Max(maxWidth, width);
                    }
                    m_listMaxWidth = maxWidth;
                }

                // Reserve the scrollable area dimensions, now that we know how
                // much horizontal space is needed.
                Rect areaRect = GUILayoutUtility.GetRect(
                    m_listMaxWidth, m_assets.Length * Styles.ItemHeight
                );

                // Get the rect used for collision checks and positioning.
                m_listRect = GetVisibleScrollArea(
                    m_scrollPos, this.position, yStart, areaRect
                );

                // Find range of visible list items. Store this now in-case the
                // scroll position changes during any of the Handle methods
                // below. (As the current position is based off when the scroll
                // view was created). Note that all changes to scroll position
                // *should* have an ExitGUI() call attached, so this is just
                // here as a failsafe.
                int visibleStart = Math.Max(
                    0, Mathf.FloorToInt(m_scrollPos.y / Styles.ItemHeight)
                );
                int visibleEnd = Math.Min(
                    m_assets.Length,
                    Mathf.CeilToInt(
                        (m_scrollPos.y + m_listRect.height) / Styles.ItemHeight
                    )
                );

                HandleMouse();
                HandleKeyboard();

                // Cache empty folder icon for each draw cycle.
                Texture2D cachedEmptyFolderIcon = null;
                
                Rect itemRect = areaRect;
                itemRect.height = Styles.ItemHeight;
                for (int i = visibleStart; i < visibleEnd; i++)
                {
                    itemRect.y = i * Styles.ItemHeight;
                    DrawListItem(i, itemRect, ref cachedEmptyFolderIcon);
                }
            }
            finally
            {
                EditorGUIUtility.SetIconSize(origIconSize);
            }

            HandleUnusedEvents();
        }

        private static Rect GetVisibleScrollArea(
            Vector2 scrollPos, Rect position, float yStart, Rect area
        )
        {
            Rect rect = new Rect(
                scrollPos.x, scrollPos.y,
                position.width, Math.Max(0f, position.height - yStart)
            );

            // Cut-off scrollbar sizes if present.
            if (area.height > rect.height)
            {
                rect.width -= GUI.skin.verticalScrollbar.fixedWidth;
                // Vertical is needed, check if horizontal is now needed.
                if (area.width > rect.width)
                {
                    rect.height -= GUI.skin.horizontalScrollbar.fixedHeight;
                }
            }
            else if (area.width > rect.width)
            {
                rect.height -= GUI.skin.horizontalScrollbar.fixedHeight;
                // Horizontal is needed, check if vertical is now needed.
                if (area.height > rect.height)
                {
                    rect.width -= GUI.skin.verticalScrollbar.fixedWidth;
                }
            }
            rect.size = Vector2.Max(Vector2.zero, rect.size);

            return rect;
        }

        private void DrawListItem(int index, Rect rect, ref Texture2D cachedEmptyFolderIcon)
        {
            var asset = m_assets[index];

            GUIStyle style = Styles.ListItem;

            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(m_listKeyboardID);
            switch (eventType)
            {
            case EventType.MouseDown:
                switch (e.button)
                {
                case 0: // Left button
                    if (rect.Contains(e.mousePosition))
                    {
                        SelectFileIndex(index, scrollIntoView: false);
                        if (e.clickCount == 2)
                        {
                            // Special behavior for double clicking file.
                            OpenFileSelection(selectFirstFile: false);
                            e.Use();
                            GUIUtility.ExitGUI();
                        }
                        else
                        {
                            BeginDragDelay(index);
                            e.Use();
                        }
                    }
                    break;
                case 1: // Right button
                    if (rect.Contains(e.mousePosition))
                    {
                        SelectFileIndex(index, scrollIntoView: false);
                        // Don't handle context menu here, otherwise selecting
                        // the new item won't be reflected with a repaint until
                        // the context menu is closed. This also matches the
                        // Project Browser's behavior, where the context menu
                        // only opens after mouse up.
                        e.Use();
                    }
                    break;
                }
                break;
            case EventType.ContextClick:
                if (rect.Contains(e.mousePosition))
                {
                    SelectFileIndex(index, scrollIntoView: false);
                    HandleContextClick(index);
                    e.Use();
                }
                break;
            case EventType.MouseDrag:
                if (TryFinishDragDelay(index))
                {
                    e.Use();
                }
                break;
            case EventType.Repaint:
                Texture2D icon = null;
                if (asset.IsEmptyFolder)
                {
                    if (cachedEmptyFolderIcon == null)
                    {
                        cachedEmptyFolderIcon = EditorGUIUtility.IconContent(
                            "FolderEmpty Icon"
                            //UnityEditor.Experimental.EditorResources.emptyFolderIconName
                        ).image as Texture2D;
                    }
                    icon = cachedEmptyFolderIcon;
                }
                if (icon == null)
                {
                    icon = asset.Icon;
                }

                GUIContent content = TempContent(asset.FileName, icon);
                style.Draw(
                    rect,
                    content,
                    on: m_selectedIndex == index,
                    // Hover only used combined with isActive for dropping.
                    isHover: false,//rect.Contains(e.mousePosition),
                    isActive: false,
                    hasKeyboardFocus:
                        GUIUtility.keyboardControl == m_listKeyboardID &&
                        EditorWindow.focusedWindow == this
                );
                break;
            }
        }

        private void BeginDragDelay(int index)
        {
            Event e = Event.current;
            m_dragIndex = index;
            m_dragStartPosition = e.mousePosition;
            GUIUtility.hotControl = m_listKeyboardID;
        }

        private bool TryFinishDragDelay(int index)
        {
            Event e = Event.current;
            if (m_dragIndex == index &&
                GUIUtility.hotControl == m_listKeyboardID &&
                // This is the same distance check used by the Project Browser
                // to initiate a file drag.
                Vector2.Distance(m_dragStartPosition, e.mousePosition) > 6f)
            {
                var asset = m_assets[m_dragIndex];

                m_dragIndex = -1;
                GUIUtility.hotControl = 0;

                string dropTitle = ObjectNames.GetDragAndDropTitle(asset.AssetObject);
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = new UnityEngine.Object[] { asset.AssetObject };
                DragAndDrop.paths = new string[] { asset.FilePath };
                DragAndDrop.StartDrag(dropTitle);
                // TODO: This repaint probably isn't necessary. But it may be
                // relevant since it involves GUIUtility.hotControl.
                Repaint();
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool CancelDragDelay()
        {
            m_dragIndex = -1;
            if (m_inGUI && m_listKeyboardID != 0 &&
                GUIUtility.hotControl == m_listKeyboardID)
            {
                GUIUtility.hotControl = 0;
                return true;
            }
            else
            {
                return false;
            }
        }

        private void HandleMouse()
        {
            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(m_listKeyboardID);
            switch (eventType)
            {
            case EventType.MouseDown:
                if (m_listRect.Contains(e.mousePosition) &&
                    GUIUtility.keyboardControl != m_listKeyboardID)
                {
                    GUIUtility.keyboardControl = m_listKeyboardID;
                    // Don't consume event. List items still need to check for
                    // mouse down.

                    // Repaint to change the list selection color from inactive
                    // to active.
                    Repaint();
                }
                break;
            case EventType.MouseUp:
                if (CancelDragDelay())
                {
                    e.Use();
                }
                break;
            }
        }

        private void HandleKeyboard()
        {
            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(m_listKeyboardID);
            if (GUIUtility.keyboardControl != m_listKeyboardID ||
                !GUI.enabled || eventType != EventType.KeyDown)
            {
                return;
            }

            // TODO: Different keycodes for OSX, like how the Project Browser
            // does it.
            switch (e.keyCode)
            {
            case KeyCode.UpArrow:
            case KeyCode.DownArrow:
            case KeyCode.PageUp:
            case KeyCode.PageDown:
            case KeyCode.Home:
            case KeyCode.End:
                if (NavigateSelectionOffset())
                {
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            case KeyCode.Backspace:
            case KeyCode.LeftArrow:
                if (NavigateUpOneFolder(selectSubFolder: true))
                {
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            case KeyCode.RightArrow:
                if (NavigateDownOneFolder(selectFirstFile: true))
                {
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (OpenFileSelection(selectFirstFile: false))
                {
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            }
        }

        private void HandleUnusedEvents()
        {
            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(m_listKeyboardID);
            switch (eventType)
            {
            case EventType.MouseDown:
                if (e.button == 0 && m_listRect.Contains(e.mousePosition))
                {
                    // Deselect if clicking space unoccupied by files. This
                    // block is only entered if no other list items consumed
                    // the MouseDown event.
                    SelectFileIndex(-1, scrollIntoView: false);
                    e.Use();
                }
                break;
            }
        }

        private void HandleContextClick(int index)
        {
            CancelDragDelay();

            var assetObject = m_assets[index].AssetObject;
            GenericMenu contextMenu = new GenericMenu();

            contextMenu.AddItem(new GUIContent("Open"), on: false, () =>
            {
                if (assetObject != null)
                {
                    var filePath = AssetDatabase.GetAssetPath(assetObject);
                    if (AssetDatabase.IsValidFolder(filePath))
                    {
                        SetFolderPath(filePath);
                    }
                    else
                    {
                        AssetDatabase.OpenAsset(assetObject);
                    }
                }
            });
            contextMenu.AddItem(new GUIContent("Show in Explorer"), on: false, () =>
            {
                if (assetObject != null)
                {
                    var filePath = AssetDatabase.GetAssetPath(assetObject);
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        EditorUtility.RevealInFinder(filePath);
                    }
                }
            });
            contextMenu.AddItem(new GUIContent("Select and Inspect"), on: false, () =>
            {
                if (assetObject != null)
                {
                    Selection.activeObject = assetObject;
                    EditorGUIUtility.PingObject(assetObject);
                }
            });
            contextMenu.AddItem(new GUIContent("Properties..."), on: false, () =>
            {
                if (assetObject != null)
                {
                    EditorUtility.OpenPropertyEditor(assetObject);
                }
            });

            contextMenu.ShowAsContext();
        }

        private bool OpenFileSelection(bool selectFirstFile)
        {
            if (SelectedAsset != null)
            {
                if (SelectedAsset.IsFolder)
                {
                    return NavigateDownOneFolder(selectFirstFile);
                }
                else
                {
                    AssetDatabase.OpenAsset(SelectedAsset.AssetObject);
                    return true;
                }
            }
            return false;
        }

        private bool NavigateDownOneFolder(bool selectFirstFile)
        {
            if (SelectedAsset != null)
            {
                if (SelectedAsset.IsFolder)
                {
                    SetFolderPath(SelectedAsset.FilePath);
                    if (selectFirstFile && m_assets.Length > 0)
                    {
                        SelectFileIndex(0, scrollIntoView: true);
                    }
                    return true;
                }
            }
            return false;
        }

        private bool NavigateUpOneFolder(bool selectSubFolder)
        {
            if (TryGetValidParentFolder(m_path, out string parentDir) &&
                m_path != parentDir)
            {
                string subFolderPath = m_path;
                SetFolderPath(parentDir);
                if (selectSubFolder)
                {
                    SelectFilePath(subFolderPath, scrollIntoView: true);
                }
                return true;
            }
            return false;
        }

        private bool NavigateSelectionOffset()
        {
            if (m_assets.Length == 0)
            {
                return false;
            }

            Event e = Event.current;
            // Select first file if nothing is selected.
            int newSelection = 0;
            if (m_selectedIndex != -1)
            {
                switch (e.keyCode)
                {
                case KeyCode.UpArrow:
                    newSelection = m_selectedIndex - 1;
                    break;
                case KeyCode.DownArrow:
                    newSelection = m_selectedIndex + 1;
                    break;
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                    // Note that Project Browser uses RoundToInt(), but this
                    // makes more sense to scroll into the first visible item.
                    int pageIncrement = Math.Max(
                        1, Mathf.FloorToInt(m_listRect.height / Styles.ItemHeight)
                    );
                    int direction = (e.keyCode == KeyCode.PageDown ? 1 : -1);
                    newSelection = m_selectedIndex + pageIncrement * direction;
                    break;
                case KeyCode.Home:
                    newSelection = 0;
                    break;
                case KeyCode.End:
                    newSelection = m_assets.Length - 1;
                    break;
                }
            }
            // Clamp selection, rather than deselecting on out-of-bounds.
            newSelection = Math.Clamp(newSelection, 0, m_assets.Length - 1);

            // Always select to scroll to index.
            SelectFileIndex(newSelection, scrollIntoView: true);
            return true;
        }
        #endregion

        #region GUI Helpers
        private static class Styles
        {
            public static readonly Vector2 IconSize = new Vector2(16f, 16f);
            public const float ItemHeight = 16f;
            public const float TopBarHeight = 21f;

            public static Texture2D TransparentIcon;

            public static GUIStyle ListItem;
            public static GUIStyle TopBar;
            public static GUIStyle AreaBg;
        }

        private static bool s_stylesInitialized = false;
        private static void InitializeStyles()
        {
            if (s_stylesInitialized)
            {
                return;
            }
            s_stylesInitialized = true;

            {
                Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                texture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                texture.Apply();
                Styles.TransparentIcon = texture;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("OL ResultLabel"));
                style.fixedHeight = Styles.ItemHeight;
                style.margin.left = 0;
                style.margin.right = 0;
                // Padding to start horizontal scrollbar before end of text.
                //style.padding.right = 4;
                Styles.ListItem = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("ProjectBrowserTopBarBg"));
                // Padding to match size of Project Browser breadcrumb bar.
                style.fixedHeight = Styles.TopBarHeight;
                style.padding.bottom += 1;

                style.fontSize = EditorStyles.label.fontSize;
                style.alignment = TextAnchor.MiddleLeft;

                Color dropTextColor = EditorStyles.whiteLabel.normal.textColor;
                // Highlight text while attempting to drop folder over bar.
                style.onNormal.textColor = dropTextColor;
                style.onHover.textColor = dropTextColor;
                style.onActive.textColor = dropTextColor;
                style.onFocused.textColor = dropTextColor;
                Styles.TopBar = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("ProjectBrowserIconAreaBg"));
                // The style is being used as-is, but it's good practice to
                // copy it anyway, in-case future changes are mistakenly
                // made without changing it back to a copy.
                Styles.AreaBg = style;
            }
        }

        private readonly static GUIContent s_tempContent = new GUIContent();
        private static GUIContent TempContent(string text, Texture2D image = null, string tooltip = null)
        {
            s_tempContent.text = text;
            s_tempContent.tooltip = tooltip;
            s_tempContent.image = image;
            return s_tempContent;
        }

        // Workaround for some annoying behaviors of Path.GetDirectoryName() in
        // Mono. Conveniently, we only ever use this together with
        // AssetDatabase.IsValidFolder().
        private static bool TryGetValidParentFolder(string path, out string parentDir)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    parentDir = NormalizePath(Path.GetDirectoryName(path));
                    if (AssetDatabase.IsValidFolder(parentDir))
                    {
                        return true;
                    }
                }
                catch {}
            }
            parentDir = null;
            return false;
        }

        private static string NormalizePath(string path)
        {
            return path?.Replace('\\', '/');
        }
        #endregion
    }
}

#endif

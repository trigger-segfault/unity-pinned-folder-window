# Unity Editor Pinned Folder Window
An isolated view for folders that won't change due to file selections, *unlike the Project window*.

Useful for keeping direct access to a specific folder where you're frequently dragging assets out of into the Inspector, etc.

## How To Use
* Place the file `PinnedFolderWindow.cs` into your Unity project in `Assets/Editor/` (or under any `Editor/` folder).
* A Pinned Folder can be opened from **Window** &gt; **Trigger Segfault** &gt; **New Pinned Folder** -or- **Assets** &gt; **Trigger Segfault** &gt; **New Pinned Folder** (Note that **Assets** is also the context menu for right clicking in the Project window).
* Opening a new Pinned Window will attempt to dock it to any existing Pinned Window tabs.
* Dragging a folder or file onto the top bar of the window (where the path is shown) will change the current path to the associated folder or parent folder respectively.
* Right clicking a file item gives options to Open it, Show in Explorer, Select and Inspect, or open the Properties window.
* Double clicking a file item behaves identically to doing so in the Project window.
* Files in this window can be dragged out of this window into any other location, however, files cannot be dragged into this window (besides for changing the current folder).

### Navigation
* Comes with most standard navigation keybinds (based on Windows keybinds).
* Additionally, Left will go up one folder, and Right will go down one.

## Image Preview

<img width="271" height="286" alt="Image of Pinned Folder window" src="https://github.com/user-attachments/assets/b58937e1-e640-4399-8690-2facf4053f05" />

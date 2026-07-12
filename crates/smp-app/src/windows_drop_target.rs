//! Windows-native OLE drag & drop for the app window, replacing Avalonia's
//! `DragDrop.DragOverEvent`/`DropEvent` (MainWindow.axaml.cs's
//! `Window_DragOver`/`Window_Drop`). Slint has no built-in support for
//! accepting OS file drops onto the window, so this registers our own
//! `IDropTarget` with the real HWND via `RegisterDragDrop`.

use std::ffi::c_void;

use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows::core::implement;
use windows::Win32::Foundation::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Ole::*;
use windows::Win32::System::SystemServices::MODIFIERKEYS_FLAGS;
use windows::Win32::UI::Shell::*;

#[implement(IDropTarget)]
struct DropHandler {
    on_drop: Box<dyn Fn(Vec<String>)>,
}

impl IDropTarget_Impl for DropHandler_Impl {
    fn DragEnter(
        &self,
        _pdataobj: windows::core::Ref<'_, IDataObject>,
        _grfkeystate: MODIFIERKEYS_FLAGS,
        _pt: &POINTL,
        pdweffect: *mut DROPEFFECT,
    ) -> windows::core::Result<()> {
        unsafe { *pdweffect = DROPEFFECT_COPY };
        Ok(())
    }

    fn DragOver(
        &self,
        _grfkeystate: MODIFIERKEYS_FLAGS,
        _pt: &POINTL,
        pdweffect: *mut DROPEFFECT,
    ) -> windows::core::Result<()> {
        unsafe { *pdweffect = DROPEFFECT_COPY };
        Ok(())
    }

    fn DragLeave(&self) -> windows::core::Result<()> {
        Ok(())
    }

    fn Drop(
        &self,
        pdataobj: windows::core::Ref<'_, IDataObject>,
        _grfkeystate: MODIFIERKEYS_FLAGS,
        _pt: &POINTL,
        pdweffect: *mut DROPEFFECT,
    ) -> windows::core::Result<()> {
        let paths = match &*pdataobj {
            Some(data_object) => extract_dropped_paths(data_object).unwrap_or_default(),
            None => Vec::new(),
        };
        unsafe {
            *pdweffect = if paths.is_empty() { DROPEFFECT_NONE } else { DROPEFFECT_COPY };
        }
        if !paths.is_empty() {
            (self.on_drop)(paths);
        }
        Ok(())
    }
}

fn extract_dropped_paths(data_object: &IDataObject) -> windows::core::Result<Vec<String>> {
    let format = FORMATETC {
        cfFormat: CF_HDROP.0,
        ptd: std::ptr::null_mut(),
        dwAspect: DVASPECT_CONTENT.0,
        lindex: -1,
        tymed: TYMED_HGLOBAL.0 as u32,
    };

    let medium = unsafe { data_object.GetData(&format)? };
    let hdrop = HDROP(unsafe { medium.u.hGlobal.0 });

    let file_count = unsafe { DragQueryFileW(hdrop, u32::MAX, None) };
    let mut paths = Vec::with_capacity(file_count as usize);

    for index in 0..file_count {
        let mut buffer = [0u16; 1024];
        let len = unsafe { DragQueryFileW(hdrop, index, Some(&mut buffer)) };
        if len > 0 {
            paths.push(String::from_utf16_lossy(&buffer[..len as usize]));
        }
    }

    unsafe {
        DragFinish(hdrop);
        ReleaseStgMedium(&medium as *const _ as *mut _);
    }

    Ok(paths)
}

/// Registers `on_drop` as the file-drop handler for `window`'s real HWND.
/// Returns `Err` (logged, not fatal) if the window handle isn't available
/// yet or registration otherwise fails -- drag & drop degrades gracefully to
/// "use Open / Add Album instead" rather than crashing the app.
///
/// winit's own Win32 backend already registers *its* `IDropTarget` on this
/// HWND (to back its cross-platform `DroppedFile`/`HoveredFile` events,
/// which Slint does not currently forward to `.slint` code -- see
/// slint-ui/slint#7328). Windows only allows one drop target per window, so
/// winit's has to be revoked first or `RegisterDragDrop` fails with
/// `DRAGDROP_E_ALREADYREGISTERED`.
pub fn register(window: &slint::Window, on_drop: impl Fn(Vec<String>) + 'static) -> windows::core::Result<()> {
    let hwnd = window_hwnd(window)?;

    unsafe {
        OleInitialize(None)?;
        let _ = RevokeDragDrop(hwnd);
    }

    let target: IDropTarget = DropHandler { on_drop: Box::new(on_drop) }.into();
    unsafe { RegisterDragDrop(hwnd, &target) }
}

/// Returns `Err` while winit hasn't created its native window yet (the
/// backend surfaces that as `HandleError::Unavailable`, which bubbles up
/// through Slint's `WindowHandle` as a generic `NotSupported` on the outer
/// call) -- callers should retry rather than treat this as permanent.
fn window_hwnd(window: &slint::Window) -> windows::core::Result<HWND> {
    let slint_handle = window.window_handle();
    let handle = slint_handle
        .window_handle()
        .map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;

    match handle.as_raw() {
        RawWindowHandle::Win32(win32_handle) => Ok(HWND(win32_handle.hwnd.get() as *mut c_void)),
        _ => Err(windows::core::Error::from(windows::Win32::Foundation::E_FAIL)),
    }
}

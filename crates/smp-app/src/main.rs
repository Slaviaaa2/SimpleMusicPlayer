// GUI subsystem for release builds (the C# csproj's `WinExe` equivalent):
// without this, Windows opens a console window alongside the app. Debug
// builds keep the console so eprintln diagnostics stay visible during dev.
#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

slint::include_modules!();

mod app_state;
mod resolve;
mod ui_bridge;
#[cfg(windows)]
mod windows_drop_target;

use smp_core::CliOptions;

fn main() -> Result<(), slint::PlatformError> {
    let raw_args: Vec<String> = std::env::args().skip(1).collect();
    if raw_args.iter().any(|arg| arg == "--uninstall") {
        run_uninstall_and_exit(&raw_args);
    }

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("failed to start the background async runtime");
    let tokio_handle = runtime.handle().clone();

    let window = AppWindow::new()?;
    app_state::init(window.as_weak(), tokio_handle);

    wire_callbacks(&window);

    let options = CliOptions::parse(std::env::args().skip(1));
    let initial_sources = resolve_initial_sources(&options);
    let shuffle = options.shuffle;
    let start_index = options.start_index;
    let loop_mode = options.loop_mode;
    let has_explicit_loop_mode = options.has_explicit_loop_mode;

    app_state::with_state(|state| {
        state.apply_initial_sources(initial_sources, shuffle, start_index, loop_mode, has_explicit_loop_mode);
    });

    // The window's HWND only exists once winit has actually created its
    // native window, which does not reliably happen within a single
    // zero-duration timer tick after show() (empirically: `window_handle()`
    // returns `HandleError::Unavailable` from the winit backend until then).
    // Retry on a short interval instead of a single deferred attempt.
    window.show()?;

    #[cfg(windows)]
    schedule_drop_target_registration(window.as_weak(), 20);

    // Same as MainWindow.axaml.cs posting OfferAppSetupIfNeededAsync at
    // background priority: let the window paint first, then offer.
    slint::Timer::single_shot(std::time::Duration::from_millis(300), || {
        app_state::with_state(|s| s.offer_app_setup_if_needed());
    });

    slint::run_event_loop()?;
    window.hide()
}

/// Console entry point for `--uninstall` (what the registry UninstallString
/// invokes). Replaces Uninstall-Release.ps1 and its same-named switches.
fn run_uninstall_and_exit(args: &[String]) -> ! {
    // Release builds run in the GUI subsystem (no console), so when invoked
    // from a terminal, reattach to it so the result message is visible.
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::Console::{AttachConsole, ATTACH_PARENT_PROCESS};
        let _ = AttachConsole(ATTACH_PARENT_PROCESS);
    }

    let options = smp_setup::UninstallOptions {
        remove_user_data: args.iter().any(|a| a == "--remove-user-data"),
        remove_cache: args.iter().any(|a| a == "--remove-cache"),
        remove_bundled_tools: args.iter().any(|a| a == "--remove-bundled-tools"),
        remove_app_directory: args.iter().any(|a| a == "--remove-app-directory"),
    };

    let result = smp_setup::run_uninstall(options);
    if result.success {
        println!("{}", result.message);
        std::process::exit(0);
    } else {
        eprintln!("{}", result.message);
        std::process::exit(1);
    }
}

#[cfg(windows)]
fn schedule_drop_target_registration(window: slint::Weak<AppWindow>, remaining_attempts: u32) {
    slint::Timer::single_shot(std::time::Duration::from_millis(50), move || {
        let Some(strong_window) = window.upgrade() else {
            return;
        };

        let result = windows_drop_target::register(strong_window.window(), |paths| {
            let _ = slint::invoke_from_event_loop(move || {
                app_state::with_state(|s| s.handle_dropped_paths(paths));
            });
        });

        match result {
            Ok(()) => {}
            Err(_) if remaining_attempts > 0 => {
                schedule_drop_target_registration(window, remaining_attempts - 1);
            }
            Err(err) => {
                eprintln!("Could not register Windows drag-and-drop target: {err}");
            }
        }
    });
}

fn resolve_initial_sources(options: &CliOptions) -> Vec<String> {
    if !options.sources.is_empty() {
        return options.sources.clone();
    }

    let current_dir = std::env::current_dir().unwrap_or_default();
    let has_media = std::fs::read_dir(&current_dir)
        .map(|entries| {
            entries
                .flatten()
                .any(|entry| entry.path().is_file() && smp_core::media_file_types::is_supported(&entry.path().to_string_lossy()))
        })
        .unwrap_or(false);

    if has_media {
        vec![current_dir.to_string_lossy().to_string()]
    } else {
        Vec::new()
    }
}

fn wire_callbacks(window: &AppWindow) {
    window.on_open_clicked(|| app_state::with_state(|s| s.open_files()));
    window.on_add_album_clicked(|| app_state::with_state(|s| s.add_album()));
    window.on_reverse_queue_clicked(|| app_state::with_state(|s| s.reverse_queue()));
    window.on_previous_clicked(|| app_state::with_state(|s| s.go_to_previous_track()));
    window.on_play_pause_clicked(|| app_state::with_state(|s| s.toggle_playback()));
    window.on_next_clicked(|| app_state::with_state(|s| s.go_to_next_track()));
    window.on_loop_clicked(|| app_state::with_state(|s| s.cycle_loop_mode()));
    window.on_add_source_clicked(|| app_state::with_state(|s| s.add_source_from_input_from_ui()));

    window.on_remove_album_history_clicked(|index| {
        app_state::with_state(|s| s.remove_album_history(index as usize));
    });
    window.on_remove_track_history_clicked(|index| {
        app_state::with_state(|s| s.remove_track_history(index as usize));
    });
    window.on_queue_item_double_clicked(|index| {
        app_state::with_state(|s| s.queue_item_double_clicked(index as usize));
    });
    window.on_album_history_double_clicked(|index| {
        app_state::with_state(|s| s.replay_album_history(index as usize));
    });
    window.on_track_history_double_clicked(|index| {
        app_state::with_state(|s| s.replay_track_history(index as usize));
    });

    window.on_dialog_confirmed(|| app_state::with_state(|s| s.dialog_confirmed()));
    window.on_dialog_cancelled(|| app_state::with_state(|s| s.dialog_cancelled()));

    window.on_seek_drag_started(|| app_state::with_state(|s| s.seek_drag_started()));
    window.on_seek_drag_finished(|value| {
        app_state::with_state(|s| s.seek_drag_finished(value as f64));
    });

    window.on_volume_changed(|value| {
        app_state::with_state(|s| s.set_volume(value as f64));
    });
    window.on_mute_changed(|muted| {
        app_state::with_state(|s| s.set_muted(muted));
    });

    window.on_key_space(|| app_state::with_state(|s| s.toggle_playback()));
    window.on_key_seek(|offset| {
        app_state::with_state(|s| s.seek_by(offset as f64));
    });
    window.on_key_track_nav(|forward| {
        app_state::with_state(|s| {
            if forward {
                s.go_to_next_track();
            } else {
                s.go_to_previous_track();
            }
        });
    });
}

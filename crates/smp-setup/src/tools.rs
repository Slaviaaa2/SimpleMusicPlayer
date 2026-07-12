//! Bundled tool download (yt-dlp / deno / ffmpeg), replacing Tools.psm1.
//! Everything here is blocking I/O -- callers run it on a worker thread.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

pub struct ToolInstallReport {
    pub installed: Vec<String>,
    pub skipped: Vec<String>,
}

pub fn install_bundled_tools(app_dir: &Path, redownload: bool) -> Result<ToolInstallReport, String> {
    let tools_root = app_dir.join("tools");
    fs::create_dir_all(&tools_root).map_err(|e| e.to_string())?;

    let mut report = ToolInstallReport { installed: Vec::new(), skipped: Vec::new() };

    install_ytdlp(&tools_root.join("yt-dlp"), redownload, &mut report)?;
    install_deno(&tools_root.join("deno"), redownload, &mut report)?;
    install_ffmpeg(&tools_root.join("ffmpeg"), redownload, &mut report)?;

    Ok(report)
}

fn install_ytdlp(dir: &Path, redownload: bool, report: &mut ToolInstallReport) -> Result<(), String> {
    let target = dir.join("yt-dlp.exe");
    if !redownload && target.is_file() {
        report.skipped.push("yt-dlp".to_string());
        return Ok(());
    }

    fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    download_to_file(crate::config::YTDLP_URL, &target)?;
    report.installed.push("yt-dlp".to_string());
    Ok(())
}

fn install_deno(dir: &Path, redownload: bool, report: &mut ToolInstallReport) -> Result<(), String> {
    let target = dir.join("deno.exe");
    if !redownload && target.is_file() {
        report.skipped.push("deno".to_string());
        return Ok(());
    }

    let archive = download_to_temp(crate::config::DENO_URL, "deno")?;
    let extract_root = extract_zip(&archive)?;
    let _ = fs::remove_file(&archive);

    let source = extract_root.join("deno.exe");
    if !source.is_file() {
        let _ = fs::remove_dir_all(&extract_root);
        return Err("deno.exe was not found after extracting the archive.".to_string());
    }

    fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    fs::copy(&source, &target).map_err(|e| e.to_string())?;
    let _ = fs::remove_dir_all(&extract_root);
    report.installed.push("deno".to_string());
    Ok(())
}

fn install_ffmpeg(dir: &Path, redownload: bool, report: &mut ToolInstallReport) -> Result<(), String> {
    let target = dir.join("ffmpeg.exe");
    if !redownload && target.is_file() {
        report.skipped.push("ffmpeg".to_string());
        return Ok(());
    }

    let archive = download_to_temp(crate::config::FFMPEG_URL, "ffmpeg")?;
    let extract_root = extract_zip(&archive)?;
    let _ = fs::remove_file(&archive);

    let Some(bin_dir) = find_directory_named(&extract_root, "bin") else {
        let _ = fs::remove_dir_all(&extract_root);
        return Err("Could not locate ffmpeg bin directory in extracted archive.".to_string());
    };

    fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    for tool_name in ["ffmpeg.exe", "ffprobe.exe", "ffplay.exe"] {
        let source = bin_dir.join(tool_name);
        if source.is_file() {
            fs::copy(&source, dir.join(tool_name)).map_err(|e| e.to_string())?;
        }
    }
    let _ = fs::remove_dir_all(&extract_root);

    if !target.is_file() {
        return Err("ffmpeg.exe was not found after extracting the archive.".to_string());
    }

    report.installed.push("ffmpeg".to_string());
    Ok(())
}

fn download_to_file(url: &str, target: &Path) -> Result<(), String> {
    let response = reqwest::blocking::Client::builder()
        .timeout(std::time::Duration::from_secs(600))
        .build()
        .map_err(|e| e.to_string())?
        .get(url)
        .send()
        .map_err(|e| format!("download of {url} failed: {e}"))?
        .error_for_status()
        .map_err(|e| format!("download of {url} failed: {e}"))?;

    let bytes = response.bytes().map_err(|e| e.to_string())?;
    fs::write(target, &bytes).map_err(|e| e.to_string())
}

fn download_to_temp(url: &str, tag: &str) -> Result<PathBuf, String> {
    let path = std::env::temp_dir().join(format!(
        "simplemusicplayer-{tag}-{}.zip",
        std::process::id()
    ));
    download_to_file(url, &path)?;
    Ok(path)
}

fn extract_zip(archive_path: &Path) -> Result<PathBuf, String> {
    let extract_root = std::env::temp_dir().join(format!(
        "simplemusicplayer-extract-{}-{}",
        std::process::id(),
        archive_path.file_stem().and_then(|s| s.to_str()).unwrap_or("archive")
    ));
    let _ = fs::remove_dir_all(&extract_root);
    fs::create_dir_all(&extract_root).map_err(|e| e.to_string())?;

    let file = fs::File::open(archive_path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    archive.extract(&extract_root).map_err(|e| e.to_string())?;

    Ok(extract_root)
}

fn find_directory_named(root: &Path, name: &str) -> Option<PathBuf> {
    let entries = fs::read_dir(root).ok()?;
    let mut subdirectories = Vec::new();

    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            if path
                .file_name()
                .and_then(|n| n.to_str())
                .map(|n| n.eq_ignore_ascii_case(name))
                .unwrap_or(false)
            {
                return Some(path);
            }
            subdirectories.push(path);
        }
    }

    subdirectories.iter().find_map(|dir| find_directory_named(dir, name))
}

pub fn remove_bundled_tools(app_dir: &Path) -> io::Result<()> {
    let tools = app_dir.join("tools");
    if tools.is_dir() {
        fs::remove_dir_all(tools)?;
    }
    Ok(())
}

fn main() {
    slint_build::compile("ui/app-window.slint").unwrap();

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("../../Assets/app-icon.ico");
        res.compile().expect("failed to embed the Windows app icon");
    }
}

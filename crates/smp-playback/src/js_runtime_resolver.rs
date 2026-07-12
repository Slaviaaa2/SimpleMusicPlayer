use crate::tool_path_resolver;

struct Candidate {
    ytdlp_runtime_name: &'static str,
    executable_name: &'static str,
}

const CANDIDATES: &[Candidate] = &[
    Candidate {
        ytdlp_runtime_name: "deno",
        executable_name: "deno",
    },
    Candidate {
        ytdlp_runtime_name: "node",
        executable_name: "node",
    },
    Candidate {
        ytdlp_runtime_name: "bun",
        executable_name: "bun",
    },
    Candidate {
        ytdlp_runtime_name: "quickjs",
        executable_name: "qjs",
    },
];

pub struct JavaScriptRuntimeSelection {
    pub runtime_name: String,
    pub executable_path: String,
}

impl JavaScriptRuntimeSelection {
    pub fn to_ytdlp_argument(&self) -> String {
        format!("{}:{}", self.runtime_name, self.executable_path)
    }
}

pub fn resolve_for_ytdlp() -> Option<JavaScriptRuntimeSelection> {
    CANDIDATES.iter().find_map(|candidate| {
        tool_path_resolver::resolve_executable_path(candidate.executable_name).map(|path| {
            JavaScriptRuntimeSelection {
                runtime_name: candidate.ytdlp_runtime_name.to_string(),
                executable_path: path.to_string_lossy().to_string(),
            }
        })
    })
}

pub fn supported_runtime_display_text() -> &'static str {
    "Deno, Node.js, Bun, or QuickJS"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn to_ytdlp_argument_formats_runtime_and_path() {
        let selection = JavaScriptRuntimeSelection {
            runtime_name: "deno".to_string(),
            executable_path: "C:\\tools\\deno.exe".to_string(),
        };
        assert_eq!(selection.to_ytdlp_argument(), "deno:C:\\tools\\deno.exe");
    }
}

use crate::LoopMode;

#[derive(Debug)]
pub struct CliOptions {
    pub sources: Vec<String>,
    pub shuffle: bool,
    pub start_index: Option<i32>,
    pub loop_mode: LoopMode,
    pub has_explicit_loop_mode: bool,
}

impl CliOptions {
    pub fn parse<I, S>(args: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        let args: Vec<String> = args.into_iter().map(Into::into).collect();
        let mut options = CliOptions {
            sources: Vec::new(),
            shuffle: false,
            start_index: None,
            loop_mode: LoopMode::All,
            has_explicit_loop_mode: false,
        };

        let mut i = 0usize;
        while i < args.len() {
            let arg = args[i].as_str();

            if !arg.starts_with("--") {
                options.sources.push(arg.to_string());
                i += 1;
                continue;
            }

            match arg {
                "--shuffle" => {
                    options.shuffle = true;
                    i += 1;
                }
                "--album" => {
                    i = match read_value(&args, i) {
                        Some((value, consumed_at)) => {
                            options.sources.push(value);
                            consumed_at + 1
                        }
                        None => i + 1,
                    };
                }
                "--index" => {
                    i = match read_value(&args, i) {
                        Some((value, consumed_at)) => {
                            if let Ok(parsed) = value.parse::<i32>() {
                                options.start_index = Some(parsed);
                            }
                            consumed_at + 1
                        }
                        None => i + 1,
                    };
                }
                "--loop" => {
                    i = match read_value(&args, i) {
                        Some((value, consumed_at)) => {
                            options.has_explicit_loop_mode = true;
                            options.loop_mode = match value.to_lowercase().as_str() {
                                "none" => LoopMode::None,
                                "one" => LoopMode::One,
                                _ => LoopMode::All,
                            };
                            consumed_at + 1
                        }
                        None => i + 1,
                    };
                }
                _ => {
                    i += 1;
                }
            }
        }

        options
    }
}

/// Returns the value token and its index, mirroring CliOptions.cs's TryReadValue:
/// on success the caller resumes scanning right after the consumed value.
fn read_value(args: &[String], flag_index: usize) -> Option<(String, usize)> {
    let next_index = flag_index + 1;
    args.get(next_index).map(|value| (value.clone(), next_index))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> CliOptions {
        CliOptions::parse(args.iter().map(|s| s.to_string()))
    }

    #[test]
    fn collects_positional_sources_and_album_flag() {
        let options = parse(&["track.mp3", "--album", "D:\\Music\\Album", "https://example.com"]);
        assert_eq!(
            options.sources,
            vec!["track.mp3", "D:\\Music\\Album", "https://example.com"]
        );
    }

    #[test]
    fn parses_shuffle_index_and_loop() {
        let options = parse(&["--shuffle", "--index", "2", "--loop", "one"]);
        assert!(options.shuffle);
        assert_eq!(options.start_index, Some(2));
        assert!(options.has_explicit_loop_mode);
        assert_eq!(options.loop_mode, LoopMode::One);
    }

    #[test]
    fn unknown_loop_value_falls_back_to_all() {
        let options = parse(&["--loop", "bogus"]);
        assert_eq!(options.loop_mode, LoopMode::All);
        assert!(options.has_explicit_loop_mode);
    }

    #[test]
    fn trailing_flag_without_value_is_ignored_without_panicking() {
        let options = parse(&["--album"]);
        assert!(options.sources.is_empty());
    }

    #[test]
    fn defaults_have_no_explicit_loop_preference() {
        let options = parse(&[]);
        assert!(!options.has_explicit_loop_mode);
        assert_eq!(options.loop_mode, LoopMode::All);
        assert_eq!(options.start_index, None);
    }
}

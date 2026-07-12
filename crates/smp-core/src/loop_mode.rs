#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoopMode {
    None,
    All,
    One,
}

impl LoopMode {
    pub fn next(self) -> LoopMode {
        match self {
            LoopMode::None => LoopMode::All,
            LoopMode::All => LoopMode::One,
            LoopMode::One => LoopMode::None,
        }
    }

    pub fn display_text(self) -> &'static str {
        match self {
            LoopMode::None => "Loop Off",
            LoopMode::One => "Loop One",
            LoopMode::All => "Loop All",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn next_cycles_none_all_one() {
        assert_eq!(LoopMode::None.next(), LoopMode::All);
        assert_eq!(LoopMode::All.next(), LoopMode::One);
        assert_eq!(LoopMode::One.next(), LoopMode::None);
    }

    #[test]
    fn display_text_matches_current_app() {
        assert_eq!(LoopMode::None.display_text(), "Loop Off");
        assert_eq!(LoopMode::All.display_text(), "Loop All");
        assert_eq!(LoopMode::One.display_text(), "Loop One");
    }
}

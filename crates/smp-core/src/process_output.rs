pub fn build_failure_details(standard_error: &str, standard_output: &str) -> String {
    build_failure_details_with_max_lines(standard_error, standard_output, 3)
}

pub fn build_failure_details_with_max_lines(
    standard_error: &str,
    standard_output: &str,
    max_lines: usize,
) -> String {
    let combined = if standard_error.trim().is_empty() {
        standard_output
    } else {
        standard_error
    };

    if combined.trim().is_empty() {
        return String::new();
    }

    let lines: Vec<&str> = combined
        .split(['\r', '\n'])
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .collect();

    let take_from = lines.len().saturating_sub(max_lines);
    format!(" {}", lines[take_from..].join(" | "))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prefers_standard_error_over_standard_output() {
        let details = build_failure_details("boom", "irrelevant");
        assert_eq!(details, " boom");
    }

    #[test]
    fn falls_back_to_standard_output_when_error_is_blank() {
        let details = build_failure_details("   ", "some output");
        assert_eq!(details, " some output");
    }

    #[test]
    fn returns_empty_string_when_both_are_blank() {
        assert_eq!(build_failure_details("", ""), "");
    }

    #[test]
    fn keeps_only_the_last_n_lines() {
        let details = build_failure_details_with_max_lines("l1\nl2\nl3\nl4", "", 2);
        assert_eq!(details, " l3 | l4");
    }
}

use crate::LoopMode;

pub fn get_previous_index(current_index: i32, queue_count: i32, loop_mode: LoopMode) -> i32 {
    if queue_count <= 0 {
        return -1;
    }

    if current_index <= 0 {
        return if loop_mode == LoopMode::None {
            0
        } else {
            queue_count - 1
        };
    }

    current_index - 1
}

pub fn get_next_index(current_index: i32, queue_count: i32, loop_mode: LoopMode) -> i32 {
    if queue_count <= 0 {
        return -1;
    }

    if current_index < 0 {
        return 0;
    }

    if current_index >= queue_count - 1 {
        return if loop_mode == LoopMode::None { -1 } else { 0 };
    }

    current_index + 1
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn next_wraps_with_loop_all() {
        assert_eq!(get_next_index(2, 3, LoopMode::All), 0);
    }

    #[test]
    fn next_stops_with_loop_none() {
        assert_eq!(get_next_index(2, 3, LoopMode::None), -1);
    }

    #[test]
    fn next_from_no_selection_starts_at_zero() {
        assert_eq!(get_next_index(-1, 3, LoopMode::All), 0);
    }

    #[test]
    fn next_on_empty_queue_is_negative() {
        assert_eq!(get_next_index(0, 0, LoopMode::All), -1);
    }

    #[test]
    fn previous_wraps_with_loop_all() {
        assert_eq!(get_previous_index(0, 3, LoopMode::All), 2);
    }

    #[test]
    fn previous_stays_at_zero_with_loop_none() {
        assert_eq!(get_previous_index(0, 3, LoopMode::None), 0);
    }

    #[test]
    fn previous_in_middle_just_decrements() {
        assert_eq!(get_previous_index(2, 5, LoopMode::All), 1);
    }
}

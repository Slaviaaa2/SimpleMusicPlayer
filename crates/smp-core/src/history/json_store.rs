use std::fs;
use std::marker::PhantomData;
use std::path::PathBuf;

use serde::de::DeserializeOwned;
use serde::Serialize;

/// Best-effort JSON persistence: load/save failures are swallowed (matching
/// JsonFileStore.cs, which never lets a corrupt or missing file crash the app).
pub struct JsonFileStore<T> {
    file_path: PathBuf,
    _marker: PhantomData<T>,
}

impl<T> JsonFileStore<T>
where
    T: Serialize + DeserializeOwned + Default,
{
    pub fn new(file_path: impl Into<PathBuf>) -> Self {
        Self {
            file_path: file_path.into(),
            _marker: PhantomData,
        }
    }

    pub fn load(&self) -> T {
        fs::read_to_string(&self.file_path)
            .ok()
            .and_then(|json| serde_json::from_str(&json).ok())
            .unwrap_or_default()
    }

    pub fn save(&self, value: &T) {
        let Ok(json) = serde_json::to_string_pretty(value) else {
            return;
        };

        if let Some(dir) = self.file_path.parent() {
            if fs::create_dir_all(dir).is_err() {
                return;
            }
        }

        let _ = fs::write(&self.file_path, json);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::{Deserialize, Serialize};

    #[derive(Debug, Default, PartialEq, Serialize, Deserialize)]
    struct Sample {
        value: String,
    }

    #[test]
    fn load_returns_default_when_file_is_missing() {
        let path = std::env::temp_dir().join(format!("smp-core-missing-{}.json", std::process::id()));
        let store: JsonFileStore<Sample> = JsonFileStore::new(path);
        assert_eq!(store.load(), Sample::default());
    }

    #[test]
    fn save_then_load_round_trips() {
        let path = std::env::temp_dir().join(format!("smp-core-roundtrip-{}.json", std::process::id()));
        let store: JsonFileStore<Sample> = JsonFileStore::new(&path);
        let value = Sample { value: "hello".to_string() };

        store.save(&value);
        assert_eq!(store.load(), value);

        std::fs::remove_file(&path).ok();
    }
}

use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    env, fs,
    path::PathBuf,
    process::{Command, ExitCode},
};

#[derive(Deserialize)]
struct Lock {
    version: u32,
    dependencies: BTreeMap<String, BTreeMap<String, Package>>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Package {
    #[serde(rename = "type")]
    kind: String,
    resolved: Option<String>,
    content_hash: Option<String>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Sources {
    lock_sha256: String,
    archives: Vec<Archive>,
}
#[derive(Serialize)]
struct Archive {
    id: String,
    version: String,
    hash: String,
}
fn safe(s: &str) -> bool {
    !s.is_empty()
        && !s.starts_with('.')
        && !s.ends_with('.')
        && !s.contains("..")
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"._-+".contains(&b))
}
fn packages(lock: Lock) -> Result<BTreeMap<(String, String), String>, String> {
    if !matches!(lock.version, 1 | 2) {
        return Err(format!("unsupported NuGet lock version {}", lock.version));
    }
    let mut packages = BTreeMap::new();
    for graph in lock.dependencies.into_values() {
        for (id, p) in graph {
            if !matches!(
                p.kind.as_str(),
                "Direct" | "Transitive" | "CentralTransitive" | "Platform"
            ) {
                return Err(format!("unsupported dependency type {} for {id}", p.kind));
            }
            let version = p
                .resolved
                .ok_or_else(|| format!("missing version for {id}"))?
                .to_ascii_lowercase();
            let content = p
                .content_hash
                .ok_or_else(|| format!("missing contentHash for {id}"))?;
            if !safe(&id) || !safe(&version) || content.is_empty() {
                return Err(format!("invalid locked package {id}"));
            }
            let key = (id.to_ascii_lowercase(), version);
            if packages
                .insert(key, content.clone())
                .is_some_and(|old| old != content)
            {
                return Err(format!("conflicting lock hashes for {id}"));
            }
        }
    }
    Ok(packages)
}
fn get(url: &str) -> Result<Value, Box<dyn std::error::Error>> {
    let res = Command::new("curl")
        .args([
            "--fail",
            "--silent",
            "--show-error",
            "--compressed",
            "--proto",
            "=https",
            "--retry",
            "3",
            url,
        ])
        .output()?;
    if !res.status.success() {
        return Err(format!("fetching {url}: {}", String::from_utf8_lossy(&res.stderr)).into());
    }
    Ok(serde_json::from_slice(&res.stdout)?)
}
fn archive(id: String, version: String) -> Result<Archive, Box<dyn std::error::Error>> {
    let leaf = get(&format!(
        "https://api.nuget.org/v3/registration5-gz-semver2/{id}/{version}.json"
    ))?;
    let entry = leaf["catalogEntry"]
        .as_str()
        .filter(|u| u.starts_with("https://api.nuget.org/v3/catalog0/"))
        .ok_or("missing NuGet.org catalog entry")?;
    let metadata = get(entry)?;
    let hash = metadata["packageHash"]
        .as_str()
        .ok_or("missing catalog packageHash")?;
    if metadata["packageHashAlgorithm"] != "SHA512"
        || hash.len() != 88
        || !hash.ends_with("==")
        || !hash.as_bytes()[..86]
            .iter().copied()
            .all(|c| c.is_ascii_alphanumeric() || b"+/".contains(&c))
    {
        return Err(format!("invalid catalog SHA-512 for {id} {version}").into());
    }
    Ok(Archive {
        id,
        version,
        hash: format!("sha512-{hash}"),
    })
}
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args_os().skip(1).collect();
    let [lock, output] = args.as_slice() else {
        return Err("usage: drvnet-pin PACKAGES.LOCK.JSON OUTPUT.JSON".into());
    };
    let bytes = fs::read(lock)?;
    let mut archives = Vec::new();
    for (id, version) in packages(serde_json::from_slice(&bytes)?)?.into_keys() {
        archives.push(archive(id, version)?);
    }
    let result = Sources {
        lock_sha256: format!("{:x}", Sha256::digest(bytes)),
        archives,
    };
    let mut json = serde_json::to_vec_pretty(&result)?;
    json.push(b'\n');
    let output = PathBuf::from(output);
    let temp = output.with_extension(format!("{}.tmp", std::process::id()));
    let mut tmp = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp)?;
    use std::io::Write;
    if let Err(e) = tmp
        .write_all(&json)
        .and_then(|_| fs::rename(&temp, &output))
    {
        let _ = fs::remove_file(temp);
        return Err(e.into());
    }
    Ok(())
}
fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("drvnet-pin: {e}");
            ExitCode::FAILURE
        }
    }
}

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::error::Error;
use std::path::{Path, PathBuf};
use std::{env, fs, process::ExitCode};

#[derive(Deserialize)]
struct RestoreGraph {
    projects: BTreeMap<String, Project>,
}
#[derive(Deserialize)]
struct Project {
    restore: Restore,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Restore {
    project_path: PathBuf,
    #[serde(default)]
    restore_lock_properties: LockProperties,
}
#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LockProperties {
    nu_get_lock_file_path: Option<PathBuf>,
}
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
fn read_lock(
    path: &Path,
    archives: &mut BTreeMap<(String, String), String>,
) -> Result<(), Box<dyn Error>> {
    let lock: Lock = serde_json::from_slice(&fs::read(path)?)?;
    if !matches!(lock.version, 1 | 2) {
        return Err(format!("unsupported NuGet lock version {}", lock.version).into());
    }
    for graph in lock.dependencies.into_values() {
        for (id, p) in graph {
            if p.kind == "Project" {
                // MSBuild includes the referenced project's own lock in the restore graph.
                continue;
            }
            if !matches!(
                p.kind.as_str(),
                "Direct" | "Transitive" | "CentralTransitive" | "Platform"
            ) {
                return Err(format!("unsupported dependency type {} for {id}", p.kind).into());
            }
            let version = p
                .resolved
                .ok_or_else(|| format!("missing version for {id}"))?
                .to_ascii_lowercase();
            let hash = p
                .content_hash
                .ok_or_else(|| format!("missing contentHash for {id}"))?;
            if !safe(&id)
                || !safe(&version)
                || hash.len() != 88
                || !hash.ends_with("==")
                || !hash.as_bytes()[..86]
                    .iter()
                    .all(|b| b.is_ascii_alphanumeric() || b"+/".contains(b))
            {
                return Err(format!("invalid locked package {id}").into());
            }
            if archives
                .insert((id.to_ascii_lowercase(), version), hash.clone())
                .is_some_and(|old| old != hash)
            {
                return Err(format!("conflicting lock hashes for {id}").into());
            }
        }
    }
    Ok(())
}
fn run() -> Result<(), Box<dyn Error>> {
    let args = env::args_os().skip(1).collect::<Vec<_>>();
    let [root, graph] = args.as_slice() else {
        return Err("usage: drvnet-plan ROOT RESTORE-GRAPH.JSON".into());
    };
    let root = fs::canonicalize(root)?;
    let graph: RestoreGraph = serde_json::from_slice(&fs::read(graph)?)?;
    let mut archives = BTreeMap::new();
    for project in graph.projects.into_values() {
        let project = project.restore;
        let dir = project
            .project_path
            .parent()
            .ok_or("invalid project path in restore graph")?;
        let name = project
            .project_path
            .file_stem()
            .ok_or("missing project name")?
            .to_str()
            .ok_or("invalid project name")?;
        let named_lock = dir.join(format!("packages.{name}.lock.json"));
        let lock = match project.restore_lock_properties.nu_get_lock_file_path {
            Some(path) => dir.join(path),
            None if named_lock.is_file() => named_lock,
            None => dir.join("packages.lock.json"),
        };
        let absolute =
            fs::canonicalize(&lock).map_err(|err| format!("{}: {err}", lock.display()))?;
        if !absolute.starts_with(&root) {
            return Err(format!("lock outside the workspace: {}", lock.display()).into());
        }
        read_lock(&absolute, &mut archives).map_err(|err| format!("{}: {err}", lock.display()))?;
    }
    let result: Vec<_> = archives
        .into_iter()
        .map(|((id, version), hash)| Archive {
            id,
            version,
            hash: format!("sha512-{hash}"),
        })
        .collect();
    serde_json::to_writer(std::io::stdout().lock(), &result)?;
    Ok(())
}
fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("drvnet: {e}");
            ExitCode::FAILURE
        }
    }
}

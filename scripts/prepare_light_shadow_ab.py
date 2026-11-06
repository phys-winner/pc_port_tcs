"""Shared hash and serialized-identity guards for current mobile asset preparers."""
import hashlib


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def identity(file):
    return file.unity_version, int(file.target_platform), [(e.path, e.guid.hex(), e.type) for e in file.externals]

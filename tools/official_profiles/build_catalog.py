#!/usr/bin/env python3
import argparse
import json
import os
import re
import tempfile


def compact(value: str) -> str:
    return "".join(character.lower() for character in value if character.isalnum())


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Merge exact game-authored CUTB actor tokens and selected sources into the official catalog.")
    parser.add_argument("selection")
    parser.add_argument("catalog")
    arguments = parser.parse_args()

    with open(arguments.selection, encoding="utf-8") as stream:
        selection = json.load(stream)
    with open(arguments.catalog, encoding="utf-8") as stream:
        catalog = json.load(stream)

    groups = catalog["groups"]
    by_token = {}
    used_ids = {group["id"] for group in groups}
    for group in groups:
        for token in group.get("actorTokens", []):
            by_token[compact(token)] = group
        for aliases in group.get("aliases", {}).values():
            for alias in aliases:
                by_token.setdefault(compact(alias), group)

    for actor in selection["Actors"]:
        actor_token = actor["ActorToken"]
        normalized = compact(actor_token)
        group = by_token.get(normalized)
        if group is None:
            base_id = re.sub(r"[^a-z0-9]+", "-", actor_token.lower()).strip("-") or "actor"
            group_id = base_id
            suffix = 2
            while group_id in used_ids:
                group_id = f"{base_id}-{suffix}"
                suffix += 1
            used_ids.add(group_id)
            group = {
                "id": group_id,
                "label": actor_token,
                "npcBaseIds": [],
                "aliases": {},
                "sources": {},
                "actorTokens": [],
            }
            groups.append(group)
            by_token[normalized] = group

        tokens = group.setdefault("actorTokens", [])
        if actor_token not in tokens:
            tokens.append(actor_token)
        sources = group.setdefault("sources", {})
        for language, values in actor["Languages"].items():
            sources[language] = [
                {
                    "scdPath": value["ScdPath"],
                    "soundNumber": value["SoundNumber"],
                    "transcript": value["Transcript"],
                    "preferred": index == 0,
                }
                for index, value in enumerate(values)
            ]

    catalog["catalogVersion"] += 1
    descriptor, temporary = tempfile.mkstemp(
        prefix=".official-voices-", suffix=".json", dir=os.path.dirname(os.path.abspath(arguments.catalog)))
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(catalog, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, arguments.catalog)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)

    print(f"catalogVersion={catalog['catalogVersion']} groups={len(groups)}")


if __name__ == "__main__":
    main()

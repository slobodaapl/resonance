from contextlib import closing
import argparse
import array
import datetime
import hashlib
import json
import math
from pathlib import Path
import sqlite3


ROOT = Path(__file__).resolve().parents[2]


def profile_hash(language, model, domain, catalog, reference):
    prefix = '\0'.join(('Designed', language, model, '', '', '', domain, str(catalog), '{}', reference['ref_text']))
    return hashlib.sha256(prefix.encode() + reference['speaker_embedding'] + reference['rvq_codes']).hexdigest()


def validate(reference, model, dimension):
    if reference['model_hash'] != model or not reference['ref_text'].strip():
        raise ValueError('Reference model or transcript is invalid')
    embedding = array.array('f', reference['speaker_embedding'])
    codes = array.array('i', reference['rvq_codes'])
    if (len(embedding) != dimension or any(not math.isfinite(v) for v in embedding)
            or reference['codebooks'] != 16 or not 0 < reference['rvq_length'] <= 250
            or len(codes) != reference['rvq_length'] * 16 or any(not 0 <= v < 2048 for v in codes)):
        raise ValueError('Reference shape or latent values are invalid')


def build(bank, source, output, pack_version):
    if output.exists() or source.resolve() == output.resolve():
        raise FileExistsError('Output must be a new database path')
    selection = json.loads((ROOT / 'assets/fallback-bank.json').read_text())
    catalog = json.loads((ROOT / 'assets/dub-profiles.json').read_text())
    protected = selection['preservedDomains']
    expected_domains = {d['id'] for d in catalog['domains']} - set(protected)
    if selection['catalogVersion'] != catalog['version'] or set(selection['domains']) != expected_domains:
        raise ValueError('Approved bank does not cover the casting catalog')
    with closing(sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True)) as original:
        metadata = original.execute('select schema_version,pack_version,catalog_version from pack_metadata').fetchall()
        if len(metadata) != 1 or metadata[0][0] != 2 or metadata[0][2] != catalog['version'] or pack_version <= metadata[0][1]:
            raise ValueError('Pack schema/catalog/version is incompatible')
        original.row_factory = sqlite3.Row
        before = {tuple(r[:6]): tuple(r) for r in original.execute('select * from fallback_profile')}
        official_count = original.execute('select count(*) from official_profile').fetchone()[0]
        scene_counts = {t: original.execute('select count(*) from ' + t).fetchone()[0] for t in ('scene_node','scene_edge')}
        models = json.loads((ROOT / 'assets/models.json').read_text())['artifacts']
        updates = []
        for model in (m for m in models if m['id'].startswith('base-')):
            quality = model['id'].removeprefix('base-')
            with closing(sqlite3.connect((bank / 'model-references' / (quality + '.sqlite3')).resolve().as_uri() + '?mode=ro', uri=True)) as references:
                references.row_factory = sqlite3.Row
                for domain, variants in selection['domains'].items():
                    for variant, identity in variants.items():
                        reference = references.execute('select * from reference_profile where id=?', (identity,)).fetchone()
                        if reference is None: raise ValueError('Missing approved reference: ' + identity)
                        validate(reference, model['sha256'], 2048 if quality.startswith('1.7b') else 1024)
                        for language in ('english','japanese','german','french'):
                            row = original.execute('select * from fallback_profile where domain_id=? and variant_id=? and language=? and model_hash=?',
                                                   (domain, variant, language, model['sha256'])).fetchone()
                            if row is None: raise ValueError(f'Missing target variant: {domain}/{variant}/{language}/{quality}')
                            updates.append((reference['ref_text'], reference['speaker_embedding'], reference['rvq_codes'], reference['rvq_length'],
                                            reference['codebooks'], profile_hash(language, model['sha256'], domain, catalog['version'], reference),
                                            domain, variant, language, model['sha256']))
        expected_keys = {(k[0],k[1],k[4],k[5]) for k in before if k[0] not in protected}
        if {tuple(u[-4:]) for u in updates} != expected_keys or len(updates) != len(expected_keys):
            raise ValueError('Approved bank must replace every unprotected variant exactly once')
        output.parent.mkdir(parents=True, exist_ok=True)
        with closing(sqlite3.connect(output, uri=True)) as result:
            result.execute('pragma temp_store=memory')
            original.backup(result)
            with result:
                for update in updates:
                    cursor = result.execute('update fallback_profile set ref_text=?,speaker_embedding=?,rvq_codes=?,rvq_length=?,codebooks=?,profile_hash=? '
                                            'where domain_id=? and variant_id=? and language=? and model_hash=?', update)
                    if cursor.rowcount != 1: raise ValueError('Approved voice update did not match exactly one row')
                result.execute('update pack_metadata set pack_version=?,created_utc=?',
                               (pack_version, datetime.datetime.now(datetime.timezone.utc).isoformat()))
            after = {tuple(r[:6]): tuple(r) for r in result.execute('select * from fallback_profile')}
            if set(before) != set(after): raise ValueError('Fallback identities changed')
            preserved = [k for k in before if k[0] in protected]
            if any(before[k] != after[k] for k in preserved): raise ValueError('Protected voice changed')
            result.execute('attach database ? as previous', (source.resolve().as_uri() + '?mode=ro',))
            for table in ('official_profile','scene_node','scene_edge'):
                difference = result.execute(f'select exists(select * from main.{table} except select * from previous.{table}) '
                                            f'or exists(select * from previous.{table} except select * from main.{table})').fetchone()[0]
                if difference: raise ValueError('Unrelated pack table changed: ' + table)
            if result.execute('pragma quick_check').fetchone()[0] != 'ok': raise ValueError('Pack database failed integrity check')
        return dict(packVersion=pack_version,catalogVersion=catalog['version'],replaced=len(updates),preserved=len(preserved),
                    officialProfiles=official_count,sceneRows=scene_counts,domains=len(selection['domains']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--bank',type=Path,required=True)
    parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--pack-version',type=int,required=True)
    args = parser.parse_args()
    print(json.dumps(build(args.bank,args.source,args.output,args.pack_version),indent=2))

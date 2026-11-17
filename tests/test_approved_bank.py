from contextlib import closing
import array
import json
from pathlib import Path
import runpy
import sqlite3
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


class ApprovedBankImportTests(unittest.TestCase):
    def setUp(self):
        artifacts = ROOT / '.references/artifacts/approved-bank-tests'
        artifacts.mkdir(parents=True, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=artifacts)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        namespace = runpy.run_path(str(ROOT / 'tools/official_profiles/import_approved_bank.py'))
        self.build = namespace['build']
        self.build.__globals__['ROOT'] = self.root
        (self.root / 'assets').mkdir()
        self.bank = self.root / 'bank'
        (self.bank / 'model-references').mkdir(parents=True)
        models = [dict(id='base-' + quality, sha256=quality) for quality in ('q4','q8','1.7b-q4','1.7b-q8')]
        self.write('models.json', dict(artifacts=models))
        self.write('dub-profiles.json', dict(version=5, domains=[dict(id=name) for name in ('generic_world','goblin')]))
        self.write('fallback-bank.json', dict(catalogVersion=5, preservedDomains=['goblin'], domains={'generic_world': {'default': 'approved'}}))
        self.source, self.output = self.root / 'source.sqlite3', self.root / 'result.sqlite3'
        with closing(sqlite3.connect(self.source)) as database, database:
            database.executescript('''
                CREATE TABLE pack_metadata(schema_version INTEGER,pack_version INTEGER,catalog_version INTEGER,created_utc TEXT);
                INSERT INTO pack_metadata VALUES(2,14,5,'original timestamp');
                CREATE TABLE fallback_profile(domain_id TEXT,variant_id TEXT,sex TEXT,age TEXT,language TEXT,model_hash TEXT,
                  ref_text TEXT,speaker_embedding BLOB,rvq_codes BLOB,rvq_length INTEGER,codebooks INTEGER,profile_hash TEXT,
                  PRIMARY KEY(domain_id,variant_id,language,model_hash));
                CREATE TABLE official_profile(id TEXT,payload BLOB);
                INSERT INTO official_profile VALUES('named',X'010203');
                CREATE TABLE scene_node(id INTEGER,payload TEXT);
                INSERT INTO scene_node VALUES(7,'native dialogue');
                CREATE TABLE scene_edge(source INTEGER,target INTEGER);
                INSERT INTO scene_edge VALUES(7,8);
            ''')
            for model in models:
                dimension = 2048 if '1.7b' in model['id'] else 1024
                embedding = array.array('f', [.25] * dimension).tobytes()
                codes = array.array('i', [31] * 16).tobytes()
                quality = model['sha256']
                with closing(sqlite3.connect(self.bank / 'model-references' / (quality + '.sqlite3'))) as references, references:
                    references.execute('CREATE TABLE reference_profile(id TEXT,model_hash TEXT,ref_text TEXT,speaker_embedding BLOB,rvq_codes BLOB,rvq_length INTEGER,codebooks INTEGER)')
                    references.execute('INSERT INTO reference_profile VALUES(?,?,?,?,?,?,?)', ('approved',quality,'new reference',embedding,codes,1,16))
                for language in ('english','japanese','german','french'):
                    for domain in ('generic_world','goblin'):
                        database.execute('INSERT INTO fallback_profile VALUES(?,?,?,?,?,?,?,?,?,?,?,?)',
                                         (domain,'default',None,None,language,quality,'original reference',embedding,codes,1,16,'f'*64))

    def write(self, name, data):
        (self.root / 'assets' / name).write_text(json.dumps(data))

    def rows(self, path, table):
        with closing(sqlite3.connect(path)) as database:
            return database.execute('SELECT * FROM ' + table).fetchall()

    def test_import_changes_approved_voices_and_preserves_every_other_table(self):
        before = self.rows(self.source, 'fallback_profile')
        report = self.build(self.bank, self.source, self.output, 15)
        self.assertEqual(report['replaced'], 16)
        self.assertEqual(report['preserved'], 16)
        self.assertEqual(before, self.rows(self.source, 'fallback_profile'))
        self.assertEqual([r for r in before if r[0]=='goblin'], [r for r in self.rows(self.output,'fallback_profile') if r[0]=='goblin'])
        for table in ('official_profile','scene_node','scene_edge'):
            self.assertEqual(self.rows(self.source,table),self.rows(self.output,table))
        self.assertTrue(all(r[6]=='new reference' for r in self.rows(self.output,'fallback_profile') if r[0]=='generic_world'))
        self.assertEqual((2,15,5), self.rows(self.output,'pack_metadata')[0][:3])

    def test_wrong_model_fails_before_creating_output(self):
        with closing(sqlite3.connect(self.bank / 'model-references/q4.sqlite3')) as references, references:
            references.execute("UPDATE reference_profile SET model_hash='wrong'")
        before = self.rows(self.source,'fallback_profile')
        with self.assertRaisesRegex(ValueError,'model'):
            self.build(self.bank,self.source,self.output,15)
        self.assertFalse(self.output.exists())
        self.assertEqual(before,self.rows(self.source,'fallback_profile'))

    def test_unmapped_variant_is_rejected_without_output(self):
        with closing(sqlite3.connect(self.source)) as database, database:
            database.execute("INSERT INTO fallback_profile SELECT domain_id,'extra',sex,age,language,model_hash,ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks,profile_hash FROM fallback_profile WHERE domain_id='generic_world'")
        with self.assertRaisesRegex(ValueError,'every unprotected variant'):
            self.build(self.bank,self.source,self.output,15)
        self.assertFalse(self.output.exists())

    def test_existing_output_is_not_overwritten(self):
        self.output.write_bytes(b'keep this existing file')
        with self.assertRaises(FileExistsError):
            self.build(self.bank,self.source,self.output,15)
        self.assertEqual(b'keep this existing file', self.output.read_bytes())


if __name__ == '__main__': unittest.main()

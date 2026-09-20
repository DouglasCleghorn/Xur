#!/usr/bin/env python3
"""Keep only the current channel candidate; never delete another workflow's data."""
import argparse,json,os,subprocess

def selected(artifacts,channel,run_id,published=False):
 prefix='release-candidate-'+channel+'-'
 return [a['id'] for a in artifacts if a['name'].startswith(prefix) and
         (a.get('workflow_run',{}).get('id',0)==run_id if published else 0<a.get('workflow_run',{}).get('id',0)<run_id)]

def main():
 p=argparse.ArgumentParser();p.add_argument('channel',choices=['nightly','stable']);p.add_argument('--published',action='store_true');a=p.parse_args()
 repo=os.environ['GITHUB_REPOSITORY'];run_id=int(os.environ['GITHUB_RUN_ID'])
 pages=json.loads(subprocess.check_output(['gh','api','--paginate','--slurp',f'repos/{repo}/actions/artifacts?per_page=100']))
 ids=selected([item for page in pages for item in page['artifacts']],a.channel,run_id,a.published)
 for identity in ids:subprocess.run(['gh','api','--method','DELETE',f'repos/{repo}/actions/artifacts/{identity}'],check=True)
 print(f'Removed {len(ids)} obsolete {a.channel} candidate artifact(s).')
if __name__=='__main__':main()

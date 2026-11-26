rm -rf /tmp/s13-build
D=/mnt/v/git/Bazaar.Workspace/wt-athanor-spikes/spikes/S13
bash $D/run.sh $D/results/linux-run$1.json 2>&1 | grep -v libEGL | tail -15
python3 $D/report.py $D/results/linux-run$1.json

curl -H "key:linus" http://swordfish.ghostpeppergames.com:1945/shutdown

start /B plink -batch -ssh ubuntu@swordfish.ghostpeppergames.com -P 1939 "nohup /opt/gpgbot-deploy/start.sh &"

pause
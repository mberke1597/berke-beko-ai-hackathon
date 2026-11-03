# Azure VM + k3s setup

Step-by-step guide for the production server: one Ubuntu 24.04 VM on Azure (Pay-As-You-Go) running k3s with Traefik, cert-manager and Let's Encrypt. GitHub Actions (`.github/workflows/deploy.yml`) deploys to it on every push to `main`.

Versions in this guide were checked on 2026-11-03:

| Component | Version | Source |
|---|---|---|
| k3s | `v1.37.1+k3s1` (bundles Traefik, CoreDNS, metrics-server, local-path storage) | https://github.com/k3s-io/k3s/releases |
| cert-manager | `v1.21.3` | https://cert-manager.io/docs/installation/kubectl/ |
| OS image | Canonical Ubuntu Server 24.04 LTS (offer `ubuntu-24_04-lts`) | Azure Marketplace |

Commands marked **laptop** run on your Windows machine in **Git Bash** (it includes `ssh`, `ssh-keygen`, `openssl` and `curl`). Commands marked **VM** run on the server over SSH. **CLI** blocks use the Azure CLI (`az`, already installed on the laptop; run `az login` first).

## 0. Decisions (fill in before you start)

| Parameter | Default suggestion | Notes |
|---|---|---|
| Resource group | `beko-rg` | fixed |
| VM name | `beko-vm` | |
| Region (`LOCATION`) | `swedencentral` or `westeurope` | the lead confirms with you |
| Size (`SIZE`) | **`Standard_D4ps_v6`** (4 vCPU, 16 GiB, Arm64 Azure Cobalt 100) | Arm64 alternative: `Standard_D4ps_v5` (4 vCPU, 16 GiB, Ampere Altra). x64 alternative: `Standard_D4as_v6` (4 vCPU, 16 GiB, AMD EPYC). Check prices in the [pricing calculator](https://azure.microsoft.com/pricing/calculator/) for your region |
| Architecture | Arm64 (matches the size) | if you pick an x64 size, set the GitHub variable `IMAGE_PLATFORM=linux/amd64` (§6) |
| OS disk | 64 GiB Standard SSD | enough for images + volumes |
| DNS label (`DNS_LABEL`) | e.g. `beko-agents` | must be unique in the region; gives `https://<label>.<region>.cloudapp.azure.com` |
| Admin user | `azureuser` | |
| SSH key | existing `~/.ssh/beko-oci.pub` | the file name does not matter to Azure; optionally rename both files to `beko-azure` / `beko-azure.pub` and use those names below |

Size names and specs are from Microsoft Learn ([Dpsv6](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/dpsv6-series), [Dpsv5](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/dpsv5-series), [Dasv6](https://learn.microsoft.com/azure/virtual-machines/sizes/general-purpose/dasv6-series)). Availability differs per region and subscription; check it before creating the VM (CLI §1B step 2, or the portal size picker shows only available sizes).

Placeholders below: `<FQDN>` = `<DNS_LABEL>.<LOCATION>.cloudapp.azure.com`, `<IP>` = the VM's public IP.

---

## 1A. Create the VM in the Azure Portal

1. https://portal.azure.com → **Create a resource** → **Virtual machine** → **Create**.
2. **Basics** tab:
   - Subscription: your Pay-As-You-Go subscription.
   - Resource group: **Create new** → `beko-rg`.
   - Virtual machine name: `beko-vm`.
   - Region: your choice from §0.
   - Availability options: **No infrastructure redundancy required**.
   - Security type: leave the default (if the portal rejects the image/size combination, choose **Standard**).
   - Image: **Ubuntu Server 24.04 LTS** (publisher Canonical). Choose the **Arm64** variant if the portal offers a choice.
   - VM architecture: **Arm64** (or **x64** if you chose an x64 size).
   - Size: **See all sizes** → search `D4ps_v6` → **Standard_D4ps_v6** → **Select**. If it is not listed, try `D4ps_v5`, or switch architecture to x64 and pick `D4as_v6`.
   - Authentication type: **SSH public key**. Username: `azureuser`.
   - SSH public key source: **Use existing public key**. Paste the output of `cat ~/.ssh/beko-oci.pub` (laptop).
   - Public inbound ports: **Allow selected ports** → **SSH (22)**, **HTTP (80)**, **HTTPS (443)**. (You tighten SSH in step 6.)
3. **Disks** tab: OS disk size **64 GiB**, OS disk type **Standard SSD (locally-redundant storage)**, **Delete with VM** checked.
4. **Networking** tab:
   - Virtual network / subnet: leave the new defaults.
   - Public IP: **Create new** → name `beko-ip`, SKU **Standard**, assignment **Static** (Standard is always static) → OK.
   - NIC network security group: **Basic**, public inbound ports 22, 80, 443 (as above).
   - **Delete public IP and NIC when VM is deleted**: leave **unchecked** if you want to keep the IP/FQDN when rebuilding the VM.
5. **Management** tab: **Enable auto-shutdown** → On, time `23:00`, your time zone, notification e-mail (see §9 Cost). → **Review + create** → **Create**.
6. **DNS name label** (free FQDN): open **beko-ip** (Resource group `beko-rg` → `beko-ip`) → **Settings → Configuration** → **DNS name label**: `beko-agents` (your `DNS_LABEL`) → **Save**. The FQDN `beko-agents.<region>.cloudapp.azure.com` is shown on the Overview page.
7. **NSG rules**: VM `beko-vm` → **Networking → Network settings** → the inbound rule for port 22 → set **Source** to **My IP address** only if you do not need GitHub Actions to SSH in (see the note below); otherwise leave Source **Any**. Rules for 80 and 443 must have Source **Any**.

**About port 22 and GitHub Actions.** The deploy job connects over SSH from GitHub-hosted runners, whose IP addresses change and come from very large ranges. If you restrict 22 to your own IP, the deploy job cannot connect. The setup in this guide keeps 22 open to the Internet but key-only (Azure Ubuntu images disable password login when you create the VM with an SSH key; §2 verifies it). If you want 22 restricted, tell the lead: the workflow would then need an Azure login (OIDC) that opens 22 for the runner's IP during the deploy.

## 1B. Same thing with the Azure CLI (alternative)

**laptop** (Git Bash):

```bash
az login
az account show --query "{name:name, id:id}" -o table   # make sure it is the Pay-As-You-Go subscription

LOCATION=swedencentral          # or westeurope
RG=beko-rg
VM=beko-vm
SIZE=Standard_D4ps_v6           # Arm64; x64: Standard_D4as_v6
IMAGE=Canonical:ubuntu-24_04-lts:server-arm64:latest   # x64: Canonical:ubuntu-24_04-lts:server:latest
DNS_LABEL=beko-agents           # unique per region
SSH_PUB=~/.ssh/beko-oci.pub

# 1. Resource group
az group create -n "$RG" -l "$LOCATION"

# 2. Check that the size and the image exist for you in this region
az vm list-skus -l "$LOCATION" --size "$SIZE" --all -o table     # "Restrictions" column must be "None"
az vm image list-skus -l "$LOCATION" -p Canonical -f ubuntu-24_04-lts -o table   # confirm the SKU in $IMAGE (server-arm64 / server)

# 3. Static Standard public IP with a DNS label
az network public-ip create -g "$RG" -n beko-ip -l "$LOCATION" \
  --sku Standard --allocation-method Static --dns-name "$DNS_LABEL"

# 4. NSG: 22 (key-only SSH, see the port 22 note in 1A), 80 and 443 from anywhere
az network nsg create -g "$RG" -n beko-nsg -l "$LOCATION"
az network nsg rule create -g "$RG" --nsg-name beko-nsg -n allow-ssh \
  --priority 100 --direction Inbound --access Allow --protocol Tcp \
  --source-address-prefixes Internet --destination-port-ranges 22
az network nsg rule create -g "$RG" --nsg-name beko-nsg -n allow-http-https \
  --priority 110 --direction Inbound --access Allow --protocol Tcp \
  --source-address-prefixes Internet --destination-port-ranges 80 443

# 5. VM
az vm create -g "$RG" -n "$VM" -l "$LOCATION" \
  --image "$IMAGE" --size "$SIZE" \
  --admin-username azureuser --ssh-key-values "$SSH_PUB" --authentication-type ssh \
  --public-ip-address beko-ip --nsg beko-nsg \
  --os-disk-size-gb 64 --storage-sku StandardSSD_LRS

# 6. Auto-shutdown every day at 21:00 UTC (= 00:00 Istanbul), with an e-mail 30 min before
az vm auto-shutdown -g "$RG" -n "$VM" --time 2100 --email "you@example.com"

# 7. FQDN and IP
az network public-ip show -g "$RG" -n beko-ip --query "{fqdn:dnsSettings.fqdn, ip:ipAddress}" -o table
```

To restrict SSH to your IP later (breaks GitHub Actions deploys, see the note in 1A):

```bash
MY_IP=$(curl -s https://api.ipify.org)
az network nsg rule update -g "$RG" --nsg-name beko-nsg -n allow-ssh --source-address-prefixes "$MY_IP/32"
```

## 1C. First SSH login (laptop)

```bash
chmod 600 ~/.ssh/beko-oci
ssh -i ~/.ssh/beko-oci azureuser@<FQDN>
```

---

## 2. Prepare the OS (VM)

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo timedatectl set-timezone Europe/Istanbul
sudo reboot   # if a kernel update was installed; then ssh in again
```

Checks:

```bash
# Password login must be off (key-only SSH)
sudo sshd -T | grep -E '^(passwordauthentication|permitrootlogin)'
#   expected: passwordauthentication no, permitrootlogin without-password (or no)

# Host firewall: Azure's Ubuntu images use the NSG as the firewall; the host has no REJECT rules
sudo ufw status            # expected: Status: inactive
sudo iptables -S | head    # expected: -P INPUT ACCEPT / -P FORWARD ACCEPT / -P OUTPUT ACCEPT, no REJECT lines
```

If `ufw` is active (it should not be), allow what k3s needs (from the [k3s requirements](https://docs.k3s.io/installation/requirements)) plus the web ports:

```bash
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw allow from 10.42.0.0/16 to any   # pods
sudo ufw allow from 10.43.0.0/16 to any   # services
```

Do not open 6443 (Kubernetes API) in the NSG; we reach it only through SSH.

---

## 3. Install k3s (VM)

```bash
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.37.1+k3s1" sh -s - server \
  --write-kubeconfig-mode 0600
```

This installs k3s as a systemd service (starts on boot) with Traefik (ingress on ports 80/443 via ServiceLB), CoreDNS, metrics-server and the `local-path` storage class. The kubeconfig is `/etc/rancher/k3s/k3s.yaml` (root only).

kubectl for `azureuser`:

```bash
mkdir -p ~/.kube
sudo cp /etc/rancher/k3s/k3s.yaml ~/.kube/config
sudo chown "$USER:$USER" ~/.kube/config
chmod 600 ~/.kube/config

kubectl get nodes            # STATUS Ready (about 30 s after install)
kubectl get pods -A          # coredns, local-path-provisioner, metrics-server, traefik: Running
kubectl get storageclass     # local-path (default)
```

Check from the **laptop**: `curl -i http://<FQDN>` must answer `404 page not found` (Traefik; no routes yet).

---

## 4. Install cert-manager (VM)

```bash
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.21.3/cert-manager.yaml
kubectl -n cert-manager rollout status deploy/cert-manager-webhook --timeout=180s
kubectl get pods -n cert-manager   # cert-manager, cert-manager-cainjector, cert-manager-webhook: Running
```

The Let's Encrypt `ClusterIssuer`s (`letsencrypt-prod`, `letsencrypt-staging`, HTTP-01 via Traefik) are in `deploy/kubernetes/cluster-issuer.yaml`; `deploy.yml` applies them on every deploy.

---

## 5. Deploy user for GitHub Actions

### 5.1 Key pair (laptop)

A separate key used only by GitHub Actions, without a passphrase:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/beko-deploy -N "" -C "github-actions-deploy"
cat ~/.ssh/beko-deploy.pub    # copy this line for the next step
```

### 5.2 User on the VM (VM)

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo install -d -m 700 -o deploy -g deploy /home/deploy/.ssh /home/deploy/.kube
echo "PASTE-THE-beko-deploy.pub-LINE-HERE" | sudo tee /home/deploy/.ssh/authorized_keys
sudo chown deploy:deploy /home/deploy/.ssh/authorized_keys
sudo chmod 600 /home/deploy/.ssh/authorized_keys

# kubectl access for deploy (cluster-admin kubeconfig; acceptable for this single-node demo cluster)
sudo cp /etc/rancher/k3s/k3s.yaml /home/deploy/.kube/config
sudo chown deploy:deploy /home/deploy/.kube/config
sudo chmod 600 /home/deploy/.kube/config
```

### 5.3 Test it (laptop)

```bash
ssh -i ~/.ssh/beko-deploy deploy@<FQDN> kubectl get nodes
ssh-keyscan -t ed25519 <FQDN>     # output = value of the DEPLOY_KNOWN_HOSTS secret
```

---

## 6. GitHub configuration

Repository → **Settings** → **Secrets and variables** → **Actions**.

**Secrets** (New repository secret). Generate random values in Git Bash and keep a copy in your password manager:

| Secret | Value | How to generate |
|---|---|---|
| `DEPLOY_HOST` | `<FQDN>` (e.g. `beko-agents.swedencentral.cloudapp.azure.com`) | §1A step 6 / §1B step 7 |
| `DEPLOY_USER` | `deploy` | — |
| `DEPLOY_SSH_KEY` | full content of `~/.ssh/beko-deploy` (the **private** key, including the `BEGIN`/`END` lines) | `cat ~/.ssh/beko-deploy` |
| `DEPLOY_KNOWN_HOSTS` | output of `ssh-keyscan -t ed25519 <FQDN>` (recommended; without it the workflow trusts the key on first contact) | §5.3 |
| `ANTHROPIC_API_KEY` | Anthropic API key | console.anthropic.com |
| `JWT_SIGNING_KEY` | ≥ 32 random bytes | `openssl rand -base64 48` |
| `POSTGRES_PASSWORD` | hex only (goes into a connection string) | `openssl rand -hex 24` |
| `MONGO_PASSWORD` | hex only (goes into a `mongodb://` URI) | `openssl rand -hex 24` |
| `GRAFANA_ADMIN_PASSWORD` | Grafana `admin` password | `openssl rand -hex 16` |
| `GHCR_PULL_TOKEN` | optional, only if the GHCR packages stay private: classic PAT with `read:packages` | github.com/settings/tokens |

**Variables** (Variables tab), all optional:

| Variable | When to set it |
|---|---|
| `IMAGE_PLATFORM` | `linux/amd64` if the VM is x64. Default `linux/arm64` (built on native `ubuntu-24.04-arm` runners) |
| `PUBLIC_HOST` | TLS host name, if different from `DEPLOY_HOST`. Default: `DEPLOY_HOST` when it is a host name; `<DEPLOY_HOST>.sslip.io` when `DEPLOY_HOST` is an IPv4 address (fallback without a DNS label) |

The deploy job uses the GitHub **environment** `production` (created automatically on the first run). To approve every deploy by hand: Settings → Environments → production → Required reviewers.

Postgres and MongoDB set their passwords only on the **first** start (empty volume). Changing `POSTGRES_PASSWORD` / `MONGO_PASSWORD` later does not change the database users (see Troubleshooting).

---

## 7. First deploy

1. The lead merges `dev` into `main` (only after your approval), or: Actions → **Deploy** → **Run workflow** on `main`.
2. Job **build** pushes `ghcr.io/mberke1597/beko-backend` and `beko-frontend` with tags `<git-sha>` and `latest` for `IMAGE_PLATFORM`.
3. **Make the packages public** (once, right after the first build; the repository is public anyway): github.com/mberke1597 → **Packages** → `beko-backend` → **Package settings** → **Change visibility** → Public. Same for `beko-frontend`. Otherwise pods stay in `ImagePullBackOff` (or set the `GHCR_PULL_TOKEN` secret). Then re-run the failed **deploy** job.
4. Job **deploy** renders `deploy/kubernetes` (kustomize + `envsubst` for `${PUBLIC_HOST}` and `${IMAGE_TAG}`), applies the ClusterIssuers, the `beko-secrets` Secret and the app over SSH, waits for `kubectl rollout status` and calls `https://<FQDN>/api/version`.

The VM must be running (not deallocated) during a deploy.

---

## 8. Check that it works

On the VM (or `ssh -i ~/.ssh/beko-deploy deploy@<FQDN> ...`):

```bash
kubectl get nodes
kubectl -n beko get pods -o wide        # all Running, READY 1/1
kubectl -n beko get pvc                 # all Bound (local-path)
kubectl -n beko get ingress,certificate # certificate beko-tls READY=True
kubectl top pods -n beko
free -h
```

From the laptop:

```bash
curl -I http://<FQDN>                   # 301 -> https
curl https://<FQDN>/health              # Healthy
curl https://<FQDN>/health/ready        # Healthy (Postgres + Mongo reachable)
curl https://<FQDN>/api/version
```

Browser: `https://<FQDN>` (Beko login shell) and `https://<FQDN>/grafana/` (user `admin`, password `GRAFANA_ADMIN_PASSWORD`).

Useful commands:

```bash
kubectl -n beko logs deploy/backend --tail=100 -f
kubectl -n beko rollout restart deploy/backend     # e.g. after changing secrets
kubectl -n beko describe pod <pod>
sudo systemctl status k3s ; sudo journalctl -u k3s -n 100
```

---

## 9. Cost control

Pay-As-You-Go bills the VM per second while it is running. Three safeguards:

### 9.1 Auto-shutdown

Set in §1A step 5 or §1B step 6. Change it later: VM → **Operations → Auto-shutdown** → On, time, time zone, notification e-mail → **Save**. Auto-shutdown **deallocates** the VM (compute billing stops). It does not start the VM again; start it when you need it (§9.3).

### 9.2 Budget with an e-mail alert

Portal: search **Budgets** (Cost Management) → scope: your subscription (or resource group `beko-rg`) → **+ Add**:

- Name `beko-monthly`, reset period **Monthly**, amount: the monthly limit you are comfortable with.
- Alert conditions: **Actual** at 50 %, 80 %, 100 %; **Forecasted** at 100 %.
- Alert recipients: your e-mail → **Create**.

A budget only alerts; it does not stop resources. Cost data lags by several hours.

### 9.3 Deallocate when not in use

```bash
az vm deallocate -g beko-rg -n beko-vm   # stop paying for compute
az vm start      -g beko-rg -n beko-vm   # k3s and all pods come back by themselves (2-3 min)
az vm get-instance-view -g beko-rg -n beko-vm --query "instanceView.statuses[1].displayStatus" -o tsv
```

Portal: VM → **Stop** (this deallocates) / **Start**. Use **Stop** in Azure, not `sudo shutdown` inside the VM: a shutdown from inside leaves the VM "Stopped" but still allocated and billed.

While deallocated you still pay for the OS disk and the static public IP (small amounts). The IP and the FQDN stay the same, so no secret changes are needed after a restart.

To remove everything: `az group delete -n beko-rg` (deletes the VM, disk, IP and all data).

---

## Troubleshooting

### Size not available / quota exceeded when creating the VM

- "The requested size ... is not available in location": try the other region from §0, or `Standard_D4ps_v5`, or the x64 `Standard_D4as_v6` (then set `IMAGE_PLATFORM=linux/amd64`). Check with `az vm list-skus -l <region> --size Standard_D4 --all -o table`.
- "Operation could not be completed as it results in exceeding approved ... Cores quota": new subscriptions have small per-family vCPU quotas. Portal → **Quotas** → **Compute** → filter your region and the family (e.g. "Standard Dpsv6 Family vCPUs") → request at least 4 → usually approved automatically within minutes. CLI view: `az vm list-usage -l <region> -o table`.

### Ports 80/443 not reachable

1. NSG has inbound Allow rules for 80 and 443 from Any/Internet (VM → Networking → Network settings). If both a subnet NSG and a NIC NSG exist, **both** must allow the ports.
2. `sudo ufw status` is inactive and `sudo iptables -S` has no REJECT rules (§2).
3. Traefik runs: `kubectl -n kube-system get pods,svc | grep traefik` (service `traefik` type LoadBalancer with the VM's private IP as EXTERNAL-IP, ports 80/443).
4. On the VM: `curl -i http://localhost` → Traefik 404 or a redirect.

### Certificate stays pending (`READY=False`)

```bash
kubectl -n beko get certificate,certificaterequest,order,challenge
kubectl -n beko describe challenge
kubectl -n cert-manager logs deploy/cert-manager --tail=100
```

- Challenge pending with a connection error: Let's Encrypt cannot reach `http://<FQDN>/.well-known/acme-challenge/...` on port **80**. Fix the NSG (above). Test from the laptop: `curl -i http://<FQDN>/.well-known/acme-challenge/test` must return 404 from Traefik, not time out.
- DNS: `nslookup <FQDN>` must return the VM's public IP. If the DNS label is missing, set it (§1A step 6).
- `429` / rate limited: switch temporarily to staging: change `cert-manager.io/cluster-issuer` in `deploy/kubernetes/ingress.yaml` to `letsencrypt-staging` (browser warning, but proves the setup), or wait.
- Fallback host without the Azure DNS label: set the GitHub variable `PUBLIC_HOST=<IP>.sslip.io` and re-run the deploy.
- After fixing the cause: `kubectl -n beko delete certificaterequest --all` to retry immediately.

Until the certificate is issued Traefik serves its self-signed default certificate (browser warning).

### `ImagePullBackOff` / `exec format error`

- `ImagePullBackOff`: GHCR packages are private. Make them public (§7 step 3) or set `GHCR_PULL_TOKEN`, then re-run the deploy job.
- `exec format error` in pod logs: image architecture does not match the VM. Arm64 VM → `IMAGE_PLATFORM` unset or `linux/arm64`; x64 VM → `linux/amd64`. Re-run the whole Deploy workflow (build + deploy).

### Backend not ready (`0/1`, readiness `/health/ready` fails)

`kubectl -n beko logs deploy/backend`. Usually database authentication: the passwords in `beko-secrets` must match what the databases were initialised with. Either set the GitHub secrets back, or (**data loss**) re-initialise: `kubectl -n beko delete statefulset postgres mongodb && kubectl -n beko delete pvc data-postgres-0 data-mongodb-0`, then re-run the deploy.

### Pods `Pending` / `OOMKilled`

`kubectl describe pod <pod>` shows the reason. Requests/limits follow Planning/Proposal.md §5 (total limits about 6.8 GB, kept even though the VM has 16 GiB). Check `free -h` and `kubectl top pods -A`.

### GitHub Actions cannot SSH

- Timeout: VM deallocated (start it), or the NSG rule for 22 is restricted to your IP (see the note in §1A).
- `Permission denied (publickey)`: `DEPLOY_SSH_KEY` must be the private key with first and last lines; the matching `.pub` must be in `/home/deploy/.ssh/authorized_keys` (mode 600, owner deploy).
- `Host key verification failed`: `DEPLOY_KNOWN_HOSTS` is stale (VM re-created). Run `ssh-keyscan -t ed25519 <FQDN>` again and update the secret.

### Rebuilding the VM

Keep `beko-ip` (do not delete it with the VM), create a new VM that uses it (§1B step 5 with `--public-ip-address beko-ip`), repeat §2–§5, update `DEPLOY_KNOWN_HOSTS`, re-run the deploy.
